namespace FSharp.MCP.DevKit.Server

open System
open System.IO
open System.Threading
open System.ComponentModel
open System.Threading.Tasks
open System.Threading.Channels
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Akka.Actor
open Akka.Configuration
open Akka.FSI.Contracts
open Akka.Proc.Supervisor
open FSharp.MCP.DevKit.Communication.IPC
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.Backends
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.Integration
open FSharp.MCP.DevKit.Server.ResultQuery
open FSharp.MCP.DevKit.Messages
open FSharp.MCP.DevKit.Analysis
open FSharp.MCP.DevKit.Analysis.SmartSymbolDetection
open ModelContextProtocol.Server
open Fantomas.Core

/// Module containing MCP server tools for F# Interactive code management
///
/// Recent improvements (June 2025):
/// - Fixed critical error string injection bug in all code insertion functions
/// - Added unified code insertion function (InsertCodeUnified) with comprehensive features:
///   * Pre-formatting of new code
///   * Safe line number validation
///   * Optional AST validation
///   * Post-insertion document formatting
///   * Atomic file operations with backup/rollback
/// - Updated line splitting to preserve empty lines (StringSplitOptions.None)
/// - Enhanced error handling to prevent malformed code injection
module McpFsiTools =

    [<CLIMutable>]
    type SessionLivenessRecord =
        { SessionId: string
          Status: string
          IsReachable: bool
          IsStale: bool
          ObservedAtUtc: DateTime
          NextProbeNotBeforeUtc: DateTime option
          ConsecutiveFailures: int
          RunningSinceUtc: DateTime option
          LastExecutionAt: DateTime option
          LastCheckpointId: string option
          ErrorMessage: string option }

    type private SessionLivenessCacheEntry =
        { Record: SessionLivenessRecord
          ConsecutiveFailures: int
          NextProbeNotBeforeUtc: DateTime }

    let private getAkkaClientConfig () =
        let configPath = Path.Combine(AppContext.BaseDirectory, "akka.server.conf")
        let configContent = File.ReadAllText(configPath)
        let contractConfig =
            ContractSerialization.configForAssemblies [ typeof<IMessage>.Assembly; typeof<ProcStartSpec>.Assembly ]

        contractConfig.WithFallback(ConfigurationFactory.ParseString(configContent))

    type AsyncFsiExecutionRequest =
        { AsyncId: string
          Request: ExecutionRequest
          EnqueuedAt: DateTime }

    /// Helper function to format diagnostic information into readable error messages
    let private formatErrorWithDiagnostics (baseError: string) (response: PipeResponse) =
        match response.Diagnostics with
        | Some diagnostics when diagnostics.Length > 0 ->
            let diagnosticMessages =
                diagnostics
                |> Array.map (fun d -> $"{d.Severity} at line {d.StartLine}: {d.Message}")
                |> String.concat "\n"

            $"{baseError}\n\nDiagnostics:\n{diagnosticMessages}"
        | _ -> baseError

    let private formatErrorWithResult (baseError: string) (result: FsiResult) =
        match result.Diagnostics with
        | diagnostics when diagnostics.Length > 0 ->
            let diagnosticMessages =
                diagnostics
                |> Array.map (fun d -> $"{d.Severity} at line {d.StartLine}: {d.Message}")
                |> String.concat "\n"

            $"{baseError}\n\nDiagnostics:\n{diagnosticMessages}"
        | _ -> baseError

    let private preferValueOrOutput (result: FsiResult) =
        result.Value
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace(value)))
        |> Option.defaultValue result.Output

    let private formatSessionState (state: SessionRecord) =
        let joinOrNone values =
            match values with
            | [] -> "(none)"
            | xs -> String.Join(", ", xs)

        let variables =
            match state.Variables with
            | [] -> "(none)"
            | xs -> xs |> List.map fst |> String.concat ", "

        let status =
            match state.Status with
            | SessionReady -> "SessionReady"
            | SessionBusy -> "SessionBusy"
            | SessionFaulted -> "SessionFaulted"
            | SessionMissing -> "SessionMissing"

        String.Join(
            "\n",
            [| "FSI Session State:"
               $"- SessionId: {state.SessionId}"
               $"- HostId: {state.HostId}"
               $"- AgentId: {state.AgentId}"
               $"- Status: {status}"
               $"- Search Paths: {joinOrNone state.SearchPaths}"
               $"- Referenced Assemblies: {joinOrNone state.Refs}"
               $"- Loaded Scripts: {joinOrNone state.Loads}"
               $"- Variables: {variables}" |]
        )

    let private sessionStatusToText (status: SessionStatus) =
        match status with
        | SessionReady -> "SessionReady"
        | SessionBusy -> "SessionBusy"
        | SessionFaulted -> "SessionFaulted"
        | SessionMissing -> "SessionMissing"

    /// Validates that a file has a supported F# file extension
    let private validateFSharpFileType (filePath: string) =
        let extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant()

        match extension with
        | ".fsx"
        | ".fs"
        | ".fsi" -> Ok()
        | _ -> Error(sprintf "Error: '%s' is not a supported F# file type. Expected .fsx, .fs, or .fsi file." filePath)

    /// Safely splits text while preserving line endings and empty lines
    let private splitLinesPreservingLineEndings (text: string) =
        if String.IsNullOrEmpty(text) then
            [||]
        else
            // Handle different line ending types properly
            text.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.None)

    /// Safely joins lines back together with consistent line endings
    let private joinLinesWithConsistentEndings (lines: string[]) = String.Join("\n", lines)

    /// Validates that new code can be safely inserted without breaking F# syntax
    let private validateInsertionContext (existingLines: string[]) (insertAtLine: int) (newCodeLines: string[]) =
        if insertAtLine <= 0 || insertAtLine > existingLines.Length + 1 then
            Error(sprintf "Invalid line number %d. File has %d lines." insertAtLine existingLines.Length)
        else
            // Check if we're trying to insert in the middle of a multi-line construct
            let lineBeforeInsertion =
                if insertAtLine > 1 && insertAtLine <= existingLines.Length then
                    Some(existingLines.[insertAtLine - 2].Trim())
                else
                    None

            let lineAfterInsertion =
                if insertAtLine <= existingLines.Length then
                    Some(existingLines.[insertAtLine - 1].Trim())
                else
                    None

            // Check for dangerous insertion points
            match lineBeforeInsertion, lineAfterInsertion with
            | Some before, Some after when before.EndsWith("=") && after.StartsWith("|") ->
                Error("Cannot insert code in the middle of a discriminated union definition")
            | Some before, Some after when before.Contains("type") && before.EndsWith("=") && after.StartsWith("|") ->
                Error("Cannot insert code in the middle of a type definition")
            | Some before, Some after when before.EndsWith("{") && after.StartsWith("}") ->
                Error("Cannot insert code in the middle of a record definition")
            | _ -> Ok()

    let toPipeDiagnostic (diagnostic: FsiRemoteDiagnostic) : PipeDiagnostic =
        { FileName = diagnostic.FileName
          StartLine = diagnostic.StartLine
          EndLine = diagnostic.EndLine
          StartColumn = diagnostic.StartColumn
          EndColumn = diagnostic.EndColumn
          Severity = diagnostic.Severity
          Message = diagnostic.Message }

    let toPipeResponse (requestId: string) (result: FsiRemoteResult) : PipeResponse =
        let diagnostics =
            if Array.isEmpty result.Diagnostics then
                None
            else
                result.Diagnostics |> Array.map toPipeDiagnostic |> Some

        { RequestId = requestId
          IsSuccess = result.IsSuccess
          Output = result.Output
          Errors = result.Errors
          Diagnostics = diagnostics
          Value = result.Value
          ExecutionTime = result.ExecutionTimeMs |> Option.map TimeSpan.FromMilliseconds
          Timestamp = DateTime.UtcNow }

    let toCachedFsiResult = BackendAdapters.toFsiResult

    let private toRemoteRouteDto (route: ExecutionRoute option) =
        route
        |> Option.map (fun value ->
            { AgentId = Some value.AgentId
              HostId = Some value.HostId
              SessionId = Some value.SessionId })

    type RemoteFsiClient(remoteActor: ActorSelection, logger: ILogger) =
        member _.SendCommand(command: RemoteFsiCommand) : Task<FsiRemoteCommandResponse> =
            let effectiveTimeout = defaultArg command.Timeout (TimeSpan.FromSeconds(30.0))
            let requestId = Guid.NewGuid().ToString("N")

            task {
                try
                    let request =
                        { RequestId = requestId
                          CommandType = command.CommandType
                          Payload = command.Payload
                          Route = toRemoteRouteDto command.Route
                          UsePackageTargets = command.UsePackageTargets
                          TimeoutMs = Some(int effectiveTimeout.TotalMilliseconds) }

                    return! remoteActor.Ask<FsiRemoteCommandResponse>(request, effectiveTimeout)
                with ex ->
                    logger.LogError(ex, "Failed to execute remote FSI command {CommandType}", command.CommandType)

                    return
                        { RequestId = requestId
                          HostId = command.Route |> Option.map (fun route -> route.HostId)
                          SessionId = command.Route |> Option.map (fun route -> route.SessionId)
                          Result =
                            { Output = ""
                              Errors = ex.Message
                              IsSuccess = false
                              ExecutionTimeMs = None
                              Diagnostics = [||]
                              Value = None
                              RawErrorType = Some(ex.GetType().FullName) }
                          SessionState = None }
            }

        member this.SendPipeCommand(commandType: string, payload: string, ?route: ExecutionRoute, ?usePackageTargets: bool, ?timeout: TimeSpan) : Task<PipeResponse> =
            task {
                let! response =
                    this.SendCommand(
                        { CommandType = commandType
                          Payload = payload
                          Route = route
                          UsePackageTargets = usePackageTargets
                          Timeout = timeout }
                    )

                return toPipeResponse response.RequestId response.Result
            }

        member this.ExecuteCode(code: string, ?timeout: TimeSpan) =
            this.SendPipeCommand("EXEC", code, ?timeout = timeout)

        member this.EvaluateExpression(expression: string, ?timeout: TimeSpan) =
            this.SendPipeCommand("EVAL", expression, ?timeout = timeout)

        member this.LoadScript(scriptPath: string, ?timeout: TimeSpan) =
            this.SendPipeCommand("LOAD", scriptPath, ?timeout = timeout)

        member this.ParseAndCheck(code: string, ?timeout: TimeSpan) =
            this.SendPipeCommand("PARSE", code, ?timeout = timeout)

        member this.ReferenceNugetPackage(packageName: string, ?timeout: TimeSpan) =
            this.SendPipeCommand("REFERENCE_NUGET", packageName, ?timeout = timeout)

        member this.ReferenceAssembly(assemblyPath: string, ?timeout: TimeSpan) =
            this.SendPipeCommand("REFERENCE_ASSEMBLY", assemblyPath, ?timeout = timeout)

        member this.AddSearchPath(path: string, ?timeout: TimeSpan) =
            this.SendPipeCommand("ADD_PATH", path, ?timeout = timeout)

        member this.Reset(?timeout: TimeSpan) =
            this.SendPipeCommand("RESET", "", ?timeout = timeout)

        member this.Restart(?timeout: TimeSpan) =
            this.SendPipeCommand("RESTART", "", ?timeout = timeout)

        member this.GetState(?timeout: TimeSpan) =
            this.SendPipeCommand("STATE", "", ?timeout = timeout)

        member this.IsServerAvailable() =
            try
                let response =
                    this.SendPipeCommand("PING", "", timeout = TimeSpan.FromSeconds(1.0))
                        .GetAwaiter()
                        .GetResult()

                response.IsSuccess
            with _ ->
                false

        interface IRemoteFsiClient with
            member this.SendCommand(command: RemoteFsiCommand) = this.SendCommand(command)

            member this.IsServerAvailable() = this.IsServerAvailable()

    /// Service that manages routed FSI execution, registries, and async queue
    type FsiMcpService
        (
            logger: ILogger<FsiMcpService>,
            ?enableRemoteClient: bool,
            ?procSupervisorClient: IProcSupervisorClient,
            ?fsiSupervisorClient: IFsiSupervisorClient,
            ?outputSubscriberBroker: IOutputSubscriberBroker,
            ?sessionOutputLiveStore: ISessionOutputLiveStore,
            ?sessionOutputArchiveStore: ISessionOutputArchiveStore,
            ?sessionLivenessSuccessTtl: TimeSpan,
            ?sessionLivenessFailureBaseBackoff: TimeSpan,
            ?sessionLivenessFailureMaxBackoff: TimeSpan,
            ?sessionLivenessStaleAfter: TimeSpan
        ) =
        let agentRegistry = InMemoryAgentRegistry() :> IAgentRegistry
        let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
        let sessionRegistry = InMemorySessionRegistry() :> ISessionRegistry
        let inventoryEventStore = InMemoryInventoryEventStore() :> IInventoryEventStore
        let outputSubscriberBroker =
            defaultArg outputSubscriberBroker (InMemoryOutputSubscriberBroker() :> IOutputSubscriberBroker)

        let sessionOutputLiveStore =
            defaultArg sessionOutputLiveStore (JsonLineSessionOutputLiveStore() :> ISessionOutputLiveStore)

        let sessionOutputArchiveStore =
            defaultArg sessionOutputArchiveStore (JsonLineSessionOutputArchiveStore() :> ISessionOutputArchiveStore)

        let asyncJobRegistry = InMemoryAsyncJobRegistry() :> IAsyncJobRegistry
        let resultRegistry = InMemoryResultRegistry() :> IResultRegistry
        let pathMappingRegistry = InMemoryPathMappingRegistry() :> IPathMappingRegistry
        let resultQueryService = ResultQueryService()
        let inProcBackend = InProcBackend() :> IFsiExecutionBackend

        let enableRemoteClient = defaultArg enableRemoteClient true
        let procSupervisorClientOpt = procSupervisorClient
        let fsiSupervisorClientOpt = fsiSupervisorClient

        let system, remoteClient =
            if enableRemoteClient then
                let akkaConfig = getAkkaClientConfig ()
                let actorSystem = ActorSystem.Create("McpClientSystem", akkaConfig)
                let remoteActorPath = "akka.tcp://FsiExecutionSystem@localhost:8081/user/fsiActor"
                let remoteActor = actorSystem.ActorSelection(remoteActorPath)
                Some actorSystem, Some(RemoteFsiClient(remoteActor, logger))
            else
                None, None

        let registeredBackends =
            [ yield inProcBackend
              match remoteClient with
              | Some client -> yield NetFxHostBackend(client :> IRemoteFsiClient) :> IFsiExecutionBackend
              | None -> () ]
            @
            [ match procSupervisorClientOpt, fsiSupervisorClientOpt with
              | Some procClient, Some supervisorClient ->
                  yield Net10HostBackend(hostRegistry, supervisorClient, procClient) :> IFsiExecutionBackend
              | _ -> () ]

        let backendSelector = BackendSelector(registeredBackends)
        let executionRouter =
            ExecutionRouter(agentRegistry, hostRegistry, sessionRegistry, resultRegistry, backendSelector)
        let hostProvisioningService =
            procSupervisorClientOpt
            |> Option.map (fun client -> HostProvisioningService(agentRegistry, hostRegistry, client, inventoryEventStore))

        let sessionProvisioningService = SessionProvisioningService(hostRegistry, sessionRegistry, backendSelector, inventoryEventStore)

        let asyncResultCache = AsyncFsiResultCache()
        let asyncRequestChannel = Channel.CreateUnbounded<AsyncFsiExecutionRequest>()
        let asyncProcessorGate = obj()
        let asyncProcessorCts = new CancellationTokenSource()
        let mutable asyncProcessor: Task option = None
        let mutable defaultTimeout = TimeSpan.FromSeconds(30.0)
        let sessionLivenessSuccessTtl = defaultArg sessionLivenessSuccessTtl (TimeSpan.FromSeconds(3.0))
        let sessionLivenessFailureBaseBackoff = defaultArg sessionLivenessFailureBaseBackoff (TimeSpan.FromSeconds(5.0))
        let sessionLivenessFailureMaxBackoff = defaultArg sessionLivenessFailureMaxBackoff (TimeSpan.FromSeconds(30.0))
        let sessionLivenessStaleAfter = defaultArg sessionLivenessStaleAfter (TimeSpan.FromSeconds(15.0))
        let sessionLivenessCache = Dictionary<string, SessionLivenessCacheEntry>(StringComparer.OrdinalIgnoreCase)
        let sessionLivenessCacheGate = obj()

        let resolveRoute (requestedRoute: ExecutionRoute option) = executionRouter.ResolveRoute requestedRoute

        let resolveHost (route: ExecutionRoute) =
            hostRegistry.TryGet route.HostId
            |> Option.defaultWith (fun () -> invalidOp $"Host '{route.HostId}' was not found.")

        let resolveBackend (route: ExecutionRoute) =
            let host = resolveHost route
            host, backendSelector.Resolve(host.HostKind)

        let updateSessionRecord (record: SessionRecord) =
            match sessionRegistry.TryGet(record.HostId, record.SessionId) with
            | Some _ -> sessionRegistry.Update record
            | None -> sessionRegistry.Create record |> ignore

            let observedAt = DateTime.UtcNow

            let liveness =
                { SessionId = record.SessionId
                  Status = sessionStatusToText record.Status
                  IsReachable = true
                  IsStale = false
                  ObservedAtUtc = observedAt
                  NextProbeNotBeforeUtc = Some(observedAt.Add(sessionLivenessSuccessTtl))
                  ConsecutiveFailures = 0
                  RunningSinceUtc = record.RunningSinceUtc
                  LastExecutionAt = record.LastExecutionAt
                  LastCheckpointId = record.LastCheckpointId
                  ErrorMessage = None }

            lock sessionLivenessCacheGate (fun () ->
                sessionLivenessCache[$"{record.HostId}::{record.SessionId}"] <-
                    { Record = liveness
                      ConsecutiveFailures = 0
                      NextProbeNotBeforeUtc = observedAt.Add(sessionLivenessSuccessTtl) })

        let clearSessionLivenessCache (hostId: string) (sessionId: string) =
            lock sessionLivenessCacheGate (fun () -> sessionLivenessCache.Remove($"{hostId}::{sessionId}") |> ignore)

        let clearHostSessionLivenessCache (hostId: string) =
            lock sessionLivenessCacheGate (fun () ->
                sessionLivenessCache.Keys
                |> Seq.filter (fun key -> key.StartsWith($"{hostId}::", StringComparison.OrdinalIgnoreCase))
                |> Seq.toArray
                |> Array.iter (fun key -> sessionLivenessCache.Remove(key) |> ignore))

        let tryGetCachedSessionLiveness (hostId: string) (sessionId: string) (observedAt: DateTime) =
            lock sessionLivenessCacheGate (fun () ->
                match sessionLivenessCache.TryGetValue($"{hostId}::{sessionId}") with
                | true, entry when observedAt < entry.NextProbeNotBeforeUtc ->
                    let isStale = observedAt - entry.Record.ObservedAtUtc >= sessionLivenessStaleAfter

                    Some
                        { entry.Record with
                            IsStale = isStale
                            NextProbeNotBeforeUtc = Some entry.NextProbeNotBeforeUtc
                            ConsecutiveFailures = entry.ConsecutiveFailures }
                | _ -> None)

        let recordUnreachableSessionLiveness (hostId: string) (sessionId: string) (observedAt: DateTime) (errorMessage: string) =
            lock sessionLivenessCacheGate (fun () ->
                let cacheKey = $"{hostId}::{sessionId}"

                let nextFailureCount =
                    match sessionLivenessCache.TryGetValue(cacheKey) with
                    | true, entry when not entry.Record.IsReachable -> entry.ConsecutiveFailures + 1
                    | _ -> 1

                let multiplier = Math.Pow(2.0, float (max 0 (nextFailureCount - 1)))

                let backoff =
                    TimeSpan.FromMilliseconds(
                        min
                            sessionLivenessFailureMaxBackoff.TotalMilliseconds
                            (sessionLivenessFailureBaseBackoff.TotalMilliseconds * multiplier)
                    )

                let record =
                    { SessionId = sessionId
                      Status = "Unreachable"
                      IsReachable = false
                      IsStale = false
                      ObservedAtUtc = observedAt
                      NextProbeNotBeforeUtc = Some(observedAt.Add(backoff))
                      ConsecutiveFailures = nextFailureCount
                      RunningSinceUtc = None
                      LastExecutionAt = None
                      LastCheckpointId = None
                      ErrorMessage = Some errorMessage }

                sessionLivenessCache[cacheKey] <-
                    { Record = record
                      ConsecutiveFailures = nextFailureCount
                      NextProbeNotBeforeUtc = observedAt.Add(backoff) }

                record)

        let sealSessionOutputBySessionId (sessionId: string) =
            let liveEvents =
                [ sessionOutputLiveStore.ListEvents(sessionId)
                  outputSubscriberBroker.ListEvents(sessionId) ]
                |> List.concat
                |> List.sortBy (fun eventRecord -> eventRecord.SequenceNo)
                |> List.groupBy (fun eventRecord -> eventRecord.SequenceNo)
                |> List.map (fun (_, grouped) -> grouped |> List.last)

            try
                let archive = sessionOutputArchiveStore.Seal(sessionId, liveEvents, DateTime.UtcNow)
                let _ = outputSubscriberBroker.ClearSessionEvents(sessionId)
                sessionOutputLiveStore.ClearSession(sessionId)
                Archived archive
            with ex ->
                logger.LogError(ex, "Failed to seal session output for session {SessionId}. Marking seal as pending.", sessionId)

                let pending =
                    sessionOutputArchiveStore.MarkSealPending(sessionId, liveEvents, DateTime.UtcNow, ex.ToString())

                let _ = outputSubscriberBroker.ClearSessionEvents(sessionId)
                sessionOutputLiveStore.ClearSession(sessionId)
                SealPending pending

        let createRequest
            (requestedRoute: ExecutionRoute option)
            (operationKind: OperationKind)
            (payload: string)
            (timeout: TimeSpan option)
            (usePackageTargets: bool option)
            =
            let route = resolveRoute requestedRoute

            { RequestId = Guid.NewGuid().ToString("N")
              Route = route
              OperationKind = operationKind
              Payload = payload
              Timeout = timeout
              UsePackageTargets = usePackageTargets }

        member this.ProcessAsyncRequest(request: AsyncFsiExecutionRequest) =
            task {
                asyncJobRegistry.MarkRunning(request.AsyncId, DateTime.UtcNow)

                let! record = executionRouter.RouteAndExecute(request.Request)
                asyncResultCache.[request.AsyncId] <- Some record.Result

                if record.Result.IsSuccess then
                    asyncJobRegistry.Complete(
                        request.AsyncId,
                        record.ResultId,
                        record.Result,
                        (record.CompletedAt |> Option.defaultValue DateTime.UtcNow)
                    )

                    logger.LogInformation(
                        "Async FSI request completed. AsyncId={AsyncId} RequestId={RequestId} ResultId={ResultId}",
                        request.AsyncId,
                        request.Request.RequestId,
                        record.ResultId
                    )
                else
                    asyncJobRegistry.Fail(
                        request.AsyncId,
                        record.Result,
                        (record.CompletedAt |> Option.defaultValue DateTime.UtcNow)
                    )

                    logger.LogWarning(
                        "Async FSI request failed. AsyncId={AsyncId} Error={Error}",
                        request.AsyncId,
                        record.Result.Errors
                    )
            }

        member this.RunAsyncProcessor(cancellationToken: CancellationToken) : Task =
            task {
                let reader = asyncRequestChannel.Reader

                try
                    while not cancellationToken.IsCancellationRequested do
                        let! request = reader.ReadAsync(cancellationToken).AsTask()
                        do! this.ProcessAsyncRequest(request)
                with :? OperationCanceledException ->
                    logger.LogInformation("Async FSI request processor stopped")
            }

        member this.EnsureAsyncProcessorStarted() =
            lock asyncProcessorGate (fun () ->
                match asyncProcessor with
                | Some worker when not worker.IsCompleted -> ()
                | _ ->
                    asyncProcessor <- Some(this.RunAsyncProcessor(asyncProcessorCts.Token))
                    logger.LogInformation("Async FSI request processor started"))

        /// Set the default timeout for FSI operations
        member this.SetDefaultTimeout(timeout: TimeSpan) = defaultTimeout <- timeout

        /// Get the current default timeout
        member this.DefaultTimeout = defaultTimeout

        member _.ResolveRoute(?requestedRoute: ExecutionRoute) = resolveRoute requestedRoute

        member _.SubscribeSessionOutput
            (
                subscriberId: string,
                ?fromSequenceNo: int64,
                ?includeHistory: bool,
                ?requestedRoute: ExecutionRoute
            ) =
            let route = resolveRoute requestedRoute

            outputSubscriberBroker.Subscribe
                { SessionId = route.SessionId
                  SubscriberId = subscriberId
                  FromSequenceNo = defaultArg fromSequenceNo 0L
                  IncludeHistory = defaultArg includeHistory false
                  SubscribedAt = DateTime.UtcNow }

        member _.ListSessionOutputSubscribers(?requestedRoute: ExecutionRoute) =
            let route = resolveRoute requestedRoute
            outputSubscriberBroker.ListSubscribers(route.SessionId)

        member _.ListSessionOutput
            (
                ?afterSequenceNo: int64,
                ?limit: int,
                ?requestedRoute: ExecutionRoute
            ) =
            let route = resolveRoute requestedRoute
            let afterSequenceNo = defaultArg afterSequenceNo 0L
            let limit = defaultArg limit Int32.MaxValue

            let liveEvents =
                outputSubscriberBroker.ListEvents(route.SessionId, afterSequenceNo = afterSequenceNo)

            let persistedLiveEvents =
                sessionOutputLiveStore.ListEvents(route.SessionId, afterSequenceNo = afterSequenceNo)

            let archivedEvents =
                sessionOutputArchiveStore.ListEvents(route.SessionId, afterSequenceNo = afterSequenceNo)

            let pendingEvents =
                sessionOutputArchiveStore.ListPendingEvents(route.SessionId, afterSequenceNo = afterSequenceNo)

            [ archivedEvents; pendingEvents; persistedLiveEvents; liveEvents ]
            |> List.concat
            |> List.sortBy (fun eventRecord -> eventRecord.SequenceNo)
            |> List.groupBy (fun eventRecord -> eventRecord.SequenceNo)
            |> List.map (fun (_, grouped) -> grouped |> List.last)
            |> List.truncate limit

        member _.UnsubscribeSessionOutput(subscriberId: string, ?requestedRoute: ExecutionRoute) =
            let route = resolveRoute requestedRoute
            outputSubscriberBroker.Unsubscribe(route.SessionId, subscriberId)

        member _.PublishSessionOutput
            (
                streamKind: string,
                payload: string,
                ?executionId: string,
                ?isReplay: bool,
                ?requestedRoute: ExecutionRoute
            ) =
            let route = resolveRoute requestedRoute

            let eventRecord, subscribers =
                outputSubscriberBroker.Publish(
                    { SessionId = route.SessionId
                      ExecutionId = executionId
                      SequenceNo = 0L
                      StreamKind = streamKind
                      TimestampUtc = DateTime.UtcNow
                      Payload = payload
                      IsReplay = defaultArg isReplay false }
                )

            sessionOutputLiveStore.Append(eventRecord)
            eventRecord, subscribers

        member _.SealSessionOutputArchive(?requestedRoute: ExecutionRoute) =
            let route = resolveRoute requestedRoute
            sealSessionOutputBySessionId route.SessionId

        member _.TryGetSessionOutputArchive(?requestedRoute: ExecutionRoute) =
            let route = resolveRoute requestedRoute
            sessionOutputArchiveStore.TryGetArchive(route.SessionId)

        member _.TryGetSessionOutputSealPending(?requestedRoute: ExecutionRoute) =
            let route = resolveRoute requestedRoute
            sessionOutputArchiveStore.TryGetSealPending(route.SessionId)

        member _.RecoverSessionOutputSealPending(?requestedRoute: ExecutionRoute) =
            let route = resolveRoute requestedRoute
            sessionOutputArchiveStore.RecoverSealPending(route.SessionId)

        member this.ExecuteOperation
            (
                operationKind: OperationKind,
                payload: string,
                ?timeout: TimeSpan,
                ?usePackageTargets: bool,
                ?requestedRoute: ExecutionRoute
            ) : Task<FsiExecutionRecord> =
            task {
                let request = createRequest requestedRoute operationKind payload timeout usePackageTargets
                let route = request.Route
                let host, backend = resolveBackend route

                match operationKind with
                | GetState ->
                    let! state = backend.GetSessionState(route)
                    updateSessionRecord state
                    agentRegistry.Touch route.AgentId

                    let now = DateTime.UtcNow

                    let record =
                        BackendAdapters.toExecutionRecord
                            host.BackendKind
                            request
                            now
                            (Some now)
                            (Some now)
                            host.HostId
                            route.SessionId
                            (Guid.NewGuid().ToString("N"))
                            { Output = formatSessionState state
                              Errors = ""
                              IsSuccess = true
                              ExecutionTime = None
                              Diagnostics = [||]
                              Value = None }
                            None

                    resultRegistry.Put record
                    return record
                | ResetSession ->
                    let _ = this.SealSessionOutputArchive(requestedRoute = route)
                    let! record = backend.ResetSession(route)
                    resultRegistry.Put record

                    let! state = backend.GetSessionState(route)
                    updateSessionRecord state
                    agentRegistry.Touch route.AgentId
                    return record
                | RestartHost ->
                    let _ =
                        sessionRegistry.ListByHost(host.HostId)
                        |> List.map (fun session -> sealSessionOutputBySessionId session.SessionId)

                    do! backend.RestartHost(host)
                    clearHostSessionLivenessCache host.HostId
                    agentRegistry.Touch route.AgentId

                    let now = DateTime.UtcNow
                    let record =
                        BackendAdapters.toExecutionRecord
                            host.BackendKind
                            request
                            now
                            (Some now)
                            (Some now)
                            host.HostId
                            route.SessionId
                            (Guid.NewGuid().ToString("N"))
                            { Output = $"Host '{host.HostId}' restart requested successfully"
                              Errors = ""
                              IsSuccess = true
                              ExecutionTime = None
                              Diagnostics = [||]
                              Value = None }
                            None

                    resultRegistry.Put record
                    return record
                | _ ->
                    return! executionRouter.RouteAndExecute(request)
            }

        member _.GetSessionState(?requestedRoute: ExecutionRoute) : Task<SessionRecord> =
            task {
                let route = resolveRoute requestedRoute
                let _, backend = resolveBackend route
                let! state = backend.GetSessionState(route)
                updateSessionRecord state
                agentRegistry.Touch route.AgentId
                return state
            }

        member _.TryGetResult(resultId: string) = resultRegistry.TryGet resultId

        member _.TryGetResultForAgent(agentId: string, resultId: string) =
            resultRegistry.TryGet resultId
            |> Option.filter (fun value -> value.AgentId = agentId)

        member _.ListSessionResults(?requestedRoute: ExecutionRoute) =
            let route = resolveRoute requestedRoute
            resultRegistry.ListBySession route

        member _.ListHostSessionResults(hostId: string, sessionId: string) =
            resultRegistry.ListBySession(
                { AgentId =
                    sessionRegistry.TryGet(hostId, sessionId)
                    |> Option.map (fun value -> value.AgentId)
                    |> Option.defaultWith (fun () -> invalidOp $"Session '{sessionId}' was not found under host '{hostId}'.")
                  HostId = hostId
                  SessionId = sessionId }
            )

        member _.ListAgentResults(agentId: string) = resultRegistry.ListByAgent agentId

        member _.QueryResults(request: ResultQueryRequest) =
            let loadOwnedRecords resultIds =
                resultIds
                |> List.distinct
                |> List.map (fun resultId ->
                    match resultRegistry.TryGet resultId with
                    | Some record when record.AgentId = request.AgentId -> record
                    | Some _ -> invalidOp $"Result '{resultId}' does not belong to agent '{request.AgentId}'."
                    | None -> invalidOp $"Result '{resultId}' was not found.")

            let materializeResponse (response: ResultQueryResponse) (records: FsiExecutionRecord list) =
                if not response.IsSuccess || request.Materialization <> SyntheticResult then
                    response
                else
                    let now = DateTime.UtcNow

                    let basis = records |> List.tryHead

                    let record =
                        { ResultId = Guid.NewGuid().ToString("N")
                          RequestId = request.QueryId
                          AgentId = request.AgentId
                          BackendKind = basis |> Option.map (fun value -> value.BackendKind) |> Option.defaultValue InProc
                          HostId = basis |> Option.map (fun value -> value.HostId) |> Option.defaultValue "query-host"
                          SessionId = basis |> Option.map (fun value -> value.SessionId) |> Option.defaultValue "query-session"
                          OperationKind = ResultQuery
                          SubmittedAt = now
                          StartedAt = Some now
                          CompletedAt = Some now
                          RawErrorType = None
                          Result =
                            { Output = response.MaterializedJson |> Option.defaultValue response.Output
                              Errors = ""
                              IsSuccess = true
                              ExecutionTime = None
                              Diagnostics = [||]
                              Value =
                                if String.IsNullOrWhiteSpace response.Output then
                                    None
                                else
                                    Some response.Output } }

                    resultRegistry.Put record

                    { response with
                        ProducedResultIds = [ record.ResultId ] }

            try
                let primaryRecords = loadOwnedRecords request.PrimaryResultIds
                let secondaryRecords = loadOwnedRecords request.SecondaryResultIds
                let response = resultQueryService.Run(request, primaryRecords, secondaryRecords)
                materializeResponse response (primaryRecords @ secondaryRecords)
            with ex ->
                { QueryId = request.QueryId
                  IsSuccess = false
                  Output = ""
                  Errors = ex.Message
                  ProducedResultIds = []
                  MaterializedJson = None }

        member _.RegisterAgent(agentId: string, ?displayName: string, ?metadata: IDictionary<string, string>) =
            let now = DateTime.UtcNow

            let mergedMetadata =
                metadata
                |> Option.map (fun values -> values |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq)
                |> Option.defaultValue Map.empty

            let record =
                match agentRegistry.TryGet agentId with
                | Some existing ->
                    { existing with
                        DisplayName = displayName |> Option.orElse existing.DisplayName
                        LastSeenAt = now
                        Metadata =
                            if Map.isEmpty mergedMetadata then
                                existing.Metadata
                            else
                                existing.Metadata |> Map.fold (fun state key value -> state.Add(key, value)) mergedMetadata }
                | None ->
                    { AgentId = agentId
                      DisplayName = displayName
                      CreatedAt = now
                      LastSeenAt = now
                      DefaultHostId = None
                      Metadata = mergedMetadata }

            agentRegistry.Register(record)

        member _.TryGetAgent(agentId: string) = agentRegistry.TryGet agentId

        member _.ListAgents() = agentRegistry.List()

        member _.CreateHost(agentId: string, hostKind: HostKind, spec: ProcHostSpec, ?requestedHostId: string) =
            match hostProvisioningService with
            | Some provisioning -> provisioning.CreateHost(agentId, hostKind, spec, ?requestedHostId = requestedHostId)
            | None -> invalidOp "ProcSupervisor client is not configured for this FsiMcpService instance."

        member _.TryGetHost(hostId: string) = hostRegistry.TryGet hostId

        member _.CreateSession(agentId: string, hostId: string, ?sessionId: string, ?sessionName: string) =
            task {
                let! record =
                    sessionProvisioningService.CreateSession(agentId, hostId, ?sessionId = sessionId, ?sessionName = sessionName)

                clearSessionLivenessCache record.HostId record.SessionId
                return record
            }

        member _.ListHosts(agentId: string) = hostRegistry.ListByAgent(agentId)

        member _.TryGetSession(hostId: string, sessionId: string) = sessionRegistry.TryGet(hostId, sessionId)

        member _.ListHostSessions(hostId: string) = sessionRegistry.ListByHost(hostId)

        member _.TryResolveRouteByHostSession(hostId: string, sessionId: string) : FSharp.MCP.DevKit.Core.ExecutionRoute option =
            sessionRegistry.TryGet(hostId, sessionId)
            |> Option.map (fun session ->
                ({ AgentId = session.AgentId
                   HostId = hostId
                   SessionId = sessionId }: FSharp.MCP.DevKit.Core.ExecutionRoute))

        member this.TryGetSessionStateForHostSession(hostId: string, sessionId: string) =
            task {
                match this.TryResolveRouteByHostSession(hostId, sessionId) with
                | None -> return None
                | Some route ->
                    let! state = this.GetSessionState(requestedRoute = route)
                    return Some state
            }

        member this.TryGetSessionLivenessForHostSession(hostId: string, sessionId: string) =
            task {
                let observedAt = DateTime.UtcNow

                match this.TryResolveRouteByHostSession(hostId, sessionId) with
                | None ->
                    clearSessionLivenessCache hostId sessionId

                    return
                        Some
                            { SessionId = sessionId
                              Status = "SessionMissing"
                              IsReachable = false
                              IsStale = false
                              ObservedAtUtc = observedAt
                              NextProbeNotBeforeUtc = None
                              ConsecutiveFailures = 0
                              RunningSinceUtc = None
                              LastExecutionAt = None
                              LastCheckpointId = None
                              ErrorMessage = Some $"Session '{sessionId}' was not found under host '{hostId}'." }
                | Some route ->
                    match tryGetCachedSessionLiveness hostId sessionId observedAt with
                    | Some cached -> return Some cached
                    | None ->
                        try
                            let! state = this.GetSessionState(requestedRoute = route)

                            return
                                Some
                                    { SessionId = state.SessionId
                                      Status = sessionStatusToText state.Status
                                      IsReachable = true
                                      IsStale = false
                                      ObservedAtUtc = observedAt
                                      NextProbeNotBeforeUtc = Some(observedAt.Add(sessionLivenessSuccessTtl))
                                      ConsecutiveFailures = 0
                                      RunningSinceUtc = state.RunningSinceUtc
                                      LastExecutionAt = state.LastExecutionAt
                                      LastCheckpointId = state.LastCheckpointId
                                      ErrorMessage = None }
                        with ex ->
                            return Some(recordUnreachableSessionLiveness hostId sessionId observedAt ex.Message)
            }

        member _.ListInventoryEvents(?afterSequenceId: int64, ?limit: int) =
            inventoryEventStore.List(?afterSequenceId = afterSequenceId, ?limit = limit)

        member _.ListPathMappings(?agentId: string, ?hostId: string) =
            match agentId, hostId with
            | Some value, _ -> pathMappingRegistry.ListByAgent(value)
            | None, Some value -> pathMappingRegistry.ListByHost(value)
            | None, None -> pathMappingRegistry.List()

        member this.EnsureRoute
            (
                agentId: string,
                ?displayName: string,
                ?hostId: string,
                ?sessionId: string,
                ?sessionName: string,
                ?hostKind: HostKind,
                ?hostSpec: ProcHostSpec
            ) : Task<EnsureRouteResponse> =
            task {
                let agentExisted = agentRegistry.TryGet agentId |> Option.isSome
                let agent = this.RegisterAgent(agentId, ?displayName = displayName)

                let resolvedHostId =
                    defaultArg hostId (agent.DefaultHostId |> Option.defaultValue $"{agentId}-host")

                let resolvedSessionId = defaultArg sessionId DefaultRouting.DefaultSessionId
                let hostPreviouslyExisted = hostRegistry.TryGet resolvedHostId |> Option.isSome
                let sessionPreviouslyExisted = sessionRegistry.TryGet(resolvedHostId, resolvedSessionId) |> Option.isSome

                let! host, hostCreatedByProvisioning =
                    task {
                        match hostRegistry.TryGet resolvedHostId with
                        | Some existing ->
                            if existing.AgentId <> agentId then
                                invalidOp $"Host '{resolvedHostId}' does not belong to agent '{agentId}'."

                            return existing, false
                        | None when
                            agentId = DefaultRouting.DefaultAgentId
                            && resolvedHostId = DefaultRouting.DefaultHostId
                            && resolvedSessionId = DefaultRouting.DefaultSessionId
                            ->
                            let route = resolveRoute None

                            let defaultHost =
                                hostRegistry.TryGet route.HostId
                                |> Option.defaultWith (fun () -> invalidOp $"Host '{route.HostId}' was not found.")

                            return defaultHost, false
                        | None ->
                            match hostKind, hostSpec, hostProvisioningService with
                            | Some kind, Some spec, Some _ ->
                                let! created = this.CreateHost(agentId, kind, spec, requestedHostId = resolvedHostId)
                                return created, true
                            | _, _, None ->
                                return raise (InvalidOperationException("ProcSupervisor client is not configured for this FsiMcpService instance."))
                            | _ ->
                                return
                                    raise (
                                        InvalidOperationException(
                                            $"Host '{resolvedHostId}' was not found for agent '{agentId}'. Pre-create the host with create_fsi_host, or call the service-level EnsureRoute overload with a host specification."
                                        )
                                    )
                    }

                let updatedAgent =
                    match agentRegistry.TryGet agentId with
                    | Some existing when existing.DefaultHostId = Some host.HostId -> existing
                    | Some existing ->
                        { existing with
                            LastSeenAt = DateTime.UtcNow
                            DefaultHostId = Some host.HostId }
                        |> agentRegistry.Register
                    | None ->
                        { agent with
                            LastSeenAt = DateTime.UtcNow
                            DefaultHostId = Some host.HostId }
                        |> agentRegistry.Register

                let! session, sessionCreatedByProvisioning =
                    task {
                        match sessionRegistry.TryGet(host.HostId, resolvedSessionId) with
                        | Some existing ->
                            if existing.AgentId <> agentId then
                                invalidOp $"Session '{resolvedSessionId}' does not belong to agent '{agentId}'."

                            return existing, false
                        | None ->
                            let! created =
                                this.CreateSession(
                                    agentId,
                                    host.HostId,
                                    sessionId = resolvedSessionId,
                                    ?sessionName = sessionName
                                )

                            return created, true
                    }

                let createdHost =
                    (not hostPreviouslyExisted)
                    && hostCreatedByProvisioning
                    && host.HostId = resolvedHostId

                let createdSession = (not sessionPreviouslyExisted) && (session.SessionId = resolvedSessionId || sessionCreatedByProvisioning)

                let notes =
                    [ if not agentExisted then
                          "Agent was registered during bootstrap."
                      if createdHost then
                          "Host was created through ProcSupervisor."
                      elif host.HostId = DefaultRouting.DefaultHostId && host.HostKind = InProcHost then
                          "Resolved to the legacy in-proc default route."
                      else
                          "Host already existed and was reused."
                      if createdSession then
                          "Session was created or hydrated under the ensured host."
                      else
                          "Session already existed and was reused." ]

                return
                    { Agent = updatedAgent
                      Host = host
                      Session = session
                      Route =
                        { AgentId = updatedAgent.AgentId
                          HostId = host.HostId
                          SessionId = session.SessionId }
                      CreatedAgent = not agentExisted
                      CreatedHost = createdHost
                      CreatedSession = createdSession
                      Notes = notes
                      RecommendedNextTools =
                        [ "execute_f_sharp_code_routed"
                          "execute_f_sharp_code_async_routed"
                          "evaluate_f_sharp_expression_routed"
                          "get_fsi_state_routed"
                          "fsi/hosts/{hostId}/sessions/{sessionId}"
                          "fsi/results/{resultId}" ] }
            }

        member _.GetHostHealth(hostId: string) =
            task {
                let host =
                    hostRegistry.TryGet hostId
                    |> Option.defaultWith (fun () -> invalidOp $"Host '{hostId}' was not found.")

                let backend = backendSelector.Resolve(host.HostKind)
                return! backend.HealthCheck(host)
            }

        member this.EnqueueExecuteCode(code: string, timeout: TimeSpan, ?requestedRoute: ExecutionRoute) =
            this.EnsureAsyncProcessorStarted()

            let asyncId = Guid.NewGuid().ToString("N")
            let executionRequest = createRequest requestedRoute ExecuteCode code (Some timeout) None
            let enqueuedAt = DateTime.UtcNow

            let request =
                { AsyncId = asyncId
                  Request = executionRequest
                  EnqueuedAt = enqueuedAt }

            asyncJobRegistry.Create(
                { AsyncId = asyncId
                  RequestId = executionRequest.RequestId
                  Route = executionRequest.Route
                  OperationKind = executionRequest.OperationKind
                  Payload = executionRequest.Payload
                  SubmittedAt = enqueuedAt
                  StartedAt = None
                  CompletedAt = None
                  Status = Queued
                  ResultId = None
                  Result = None }
            )
            |> ignore

            asyncResultCache.[asyncId] <- None

            if asyncRequestChannel.Writer.TryWrite(request) then
                logger.LogInformation("Async FSI request enqueued. AsyncId={AsyncId}", asyncId)
                asyncId
            else
                asyncJobRegistry.Fail(
                    asyncId,
                    (BackendAdapters.createFailedResult "Failed to enqueue async FSI request" None (Some "QueueWriteFailure")),
                    DateTime.UtcNow
                )
                asyncResultCache.TryRemove(asyncId) |> ignore
                $"ERROR: Failed to enqueue async FSI request. AsyncId={asyncId} was marked as failed."

        member this.TryGetAsyncExecution(asyncId: string) =
            match asyncResultCache.TryGetValue(asyncId) with
            | true, result -> Some result
            | false, _ -> None

        member this.GetAsyncExecutionStatus(asyncId: string) =
            match asyncJobRegistry.TryGet(asyncId) |> AsyncFsiStatus.fromJob with
            | status when not status.Exists -> { status with AsyncId = asyncId }
            | status -> status

        member _.GetClient() =
            remoteClient
            |> Option.defaultWith (fun () ->
                invalidOp "Remote FSI client is disabled for this FsiMcpService instance.")

        member _.IsRunning =
            try
                let route = resolveRoute None
                let host, backend = resolveBackend route
                backend.HealthCheck(host).GetAwaiter().GetResult().IsAvailable
            with ex ->
                logger.LogWarning(ex, "Failed to resolve default execution route for health check")
                false

        interface IDisposable with
            member _.Dispose() =
                asyncProcessorCts.Cancel()
                asyncProcessorCts.Dispose()
                match system with
                | Some actorSystem -> actorSystem.Dispose()
                | None -> ()

    /// MCP FSI Tools that provide F# Interactive functionality through named pipes
    [<McpServerToolType>]
    type FSharpInteractiveTools =

        [<McpServerTool; Description("Execute F# code in FSI and return the result")>]
        static member ExecuteFSharpCode
            (
                fsiService: FsiMcpService,
                [<Description("F# code to execute")>] code: string,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! record = fsiService.ExecuteOperation(ExecuteCode, code, timeout = timeout)
                let result = record.Result

                if result.IsSuccess then
                    let output =
                        if String.IsNullOrEmpty(result.Output) then
                            "Code executed successfully"
                        else
                            result.Output

                    return output
                else
                    let baseError =
                        if String.IsNullOrEmpty(result.Errors) then
                            "Execution failed"
                        else
                            result.Errors

                    let errorMessage = formatErrorWithResult baseError result
                    return errorMessage
            }

        [<McpServerTool(Name = "execute_f_sharp_code_async"); Description("Enqueue F# code execution and return an async id immediately. Best flow for agents: 1. Call this tool to get asyncId. 2. Poll get_async_status or read resource fsi/async/{asyncId}. 3. Continue until isCompleted becomes true. Prefer this over synchronous execute for long-running or heavy scripts.")>]
        static member ExecuteFSharpCodeAsync
            (
                fsiService: FsiMcpService,
                [<Description("F# code to execute asynchronously. After this tool returns asyncId, poll get_async_status or read resource fsi/async/{asyncId} until isCompleted is true. If the code includes #I/#r paths, they must be visible from the FSI host process, not just from the caller's container.")>] code: string,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let asyncId = fsiService.EnqueueExecuteCode(code, timeout)
                return asyncId
            }

        [<McpServerTool(Name = "get_async_status"); Description("Get async FSI execution status by asyncId. Use this when your client cannot directly call resources/read for fsi/async/{asyncId}. Works for async jobs created by both execute_f_sharp_code_async and execute_f_sharp_code_async_routed.")>]
        static member GetAsyncStatus
            (
                fsiService: FsiMcpService,
                [<Description("Async job id returned by execute_f_sharp_code_async or execute_f_sharp_code_async_routed.")>] asyncId: string
            ) : Task<string> =
            task {
                let status = fsiService.GetAsyncExecutionStatus(asyncId)
                return FSharpJson.serialize status
            }

        [<McpServerTool; Description("Execute F# code in FSI and return the result with detailed error information")>]
        static member ExecuteFSharpCodeDetailed
            (
                fsiService: FsiMcpService,
                [<Description("F# code to execute")>] code: string,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! record = fsiService.ExecuteOperation(ExecuteCode, code, timeout = timeout)
                let result = record.Result

                if result.IsSuccess then
                    let output =
                        if String.IsNullOrEmpty(result.Output) then
                            "Code executed successfully"
                        else
                            result.Output

                    return output
                else
                    // Detailed error reporting
                    let errorDetails =
                        [ yield $"=== EXECUTION FAILED ==="
                          yield $"Code: {code}"
                          yield $"IsSuccess: {result.IsSuccess}"
                          yield $"Output: '{result.Output}'"
                          yield $"Errors: '{result.Errors}'"
                          yield $"RequestId: {record.RequestId}"
                          yield $"ResultId: {record.ResultId}"
                          yield $"BackendKind: {record.BackendKind}"
                          yield $"HostId: {record.HostId}"
                          yield $"SessionId: {record.SessionId}"
                          match record.RawErrorType with
                          | Some rawErrorType -> yield $"RawErrorType: {rawErrorType}"
                          | None -> ()
                          match result.ExecutionTime with
                          | Some time -> yield $"ExecutionTime: {time.TotalMilliseconds}ms"
                          | None -> yield "ExecutionTime: Not available"
                          match result.Diagnostics with
                          | diags when diags.Length > 0 ->
                              yield $"Diagnostics: {diags.Length} items"

                              for diag in diags do
                                  yield $"  - {diag.Severity}: {diag.Message} at line {diag.StartLine}"
                          | _ -> yield "Diagnostics: None"
                          yield $"=========================" ]
                        |> String.concat "\n"

                    return errorDetails
            }

        [<McpServerTool; Description("Evaluate F# expression and return the result with type information")>]
        static member EvaluateFSharpExpression
            (
                fsiService: FsiMcpService,
                [<Description("F# expression to evaluate")>] expression: string,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! record = fsiService.ExecuteOperation(EvaluateExpression, expression, timeout = timeout)
                let result = record.Result

                if result.IsSuccess then
                    let valueOrOutput = preferValueOrOutput result
                    return valueOrOutput
                else
                    let baseError =
                        if String.IsNullOrEmpty(result.Errors) then
                            "Expression evaluation failed"
                        else
                            result.Errors

                    let errorMessage = formatErrorWithResult baseError result
                    return errorMessage
            }

        [<McpServerTool; Description("Load an F# script file using #load directive. The script path must be visible from the FSI host process; caller-local container paths may not work for remote hosts.")>]
        static member LoadFSharpScript
            (
                fsiService: FsiMcpService,
                [<Description("Path to the F# script file to load. It must be visible from the FSI host process.")>] scriptPath: string,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! record = fsiService.ExecuteOperation(LoadScript, scriptPath, timeout = timeout)
                let result = record.Result

                if result.IsSuccess then
                    let successMessage = $"Script loaded successfully: {scriptPath}"
                    return successMessage
                else
                    let baseError =
                        if String.IsNullOrEmpty(result.Errors) then
                            $"Failed to load script: {scriptPath}"
                        else
                            $"Error loading script: {result.Errors}"

                    let errorMessage = formatErrorWithResult baseError result
                    return errorMessage
            }

        [<McpServerTool; Description("Reference a .NET assembly using #r directive. If you pass a file path, it must be visible from the FSI host process.")>]
        static member ReferenceAssembly
            (
                fsiService: FsiMcpService,
                [<Description("Path to the assembly or assembly name to reference. If you pass a path, it must be visible from the FSI host process.")>] assemblyPath: string,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! record = fsiService.ExecuteOperation(ReferenceAssembly, assemblyPath, timeout = timeout)
                let result = record.Result

                if result.IsSuccess then
                    return $"Assembly referenced successfully: {assemblyPath}"
                else
                    let baseError =
                        if String.IsNullOrEmpty(result.Errors) then
                            $"Failed to reference assembly: {assemblyPath}"
                        else
                            $"Error referencing assembly: {result.Errors}"

                    let errorMessage = formatErrorWithResult baseError result
                    return errorMessage
            }

        [<McpServerTool; Description("Reference a NuGet package using #r \"nuget: PackageName\" directive")>]
        static member ReferenceNuGetPackage
            (
                fsiService: FsiMcpService,
                [<Description("NuGet package name (e.g. 'Newtonsoft.Json' or 'FSharp.Data, 4.2.7')")>] packageName:
                    string,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! record = fsiService.ExecuteOperation(ReferenceNuget, packageName, timeout = timeout)
                let result = record.Result

                if result.IsSuccess then
                    return $"NuGet package referenced successfully: {packageName}"
                else
                    let baseError =
                        if String.IsNullOrEmpty(result.Errors) then
                            $"Failed to reference NuGet package: {packageName}"
                        else
                            $"Error referencing NuGet package: {result.Errors}"

                    let errorMessage = formatErrorWithResult baseError result
                    return errorMessage
            }

        [<McpServerTool; Description("Add a directory to the F# search path using #I directive. The path must be visible from the FSI host process.")>]
        static member AddSearchPath
            (
                fsiService: FsiMcpService,
                [<Description("Directory path to add to F# search path. It must be visible from the FSI host process.")>] path: string,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! record = fsiService.ExecuteOperation(AddSearchPath, path, timeout = timeout)
                let result = record.Result

                if result.IsSuccess then
                    return $"Search path added successfully: {path}"
                else
                    let baseError =
                        if String.IsNullOrEmpty(result.Errors) then
                            $"Failed to add search path: {path}"
                        else
                            $"Error adding search path: {result.Errors}"

                    let errorMessage = formatErrorWithResult baseError result
                    return errorMessage
            }

        [<McpServerTool; Description("Parse and check F# code for syntax and type errors")>]
        static member ParseAndCheckFSharpCode
            (
                fsiService: FsiMcpService,
                [<Description("F# code to parse and check")>] code: string,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let client = fsiService.GetClient()

                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! response = client.ParseAndCheck(code, timeout)

                if response.IsSuccess then
                    match response.Diagnostics with
                    | Some diagnostics when diagnostics.Length > 0 ->
                        let diagnosticStrings =
                            diagnostics
                            |> Array.map (fun d -> $"{d.Severity} at line {d.StartLine}: {d.Message}")

                        let diagnosticStr = String.Join("\n", diagnosticStrings)
                        return $"Code parsed successfully with diagnostics:\n{diagnosticStr}"
                    | _ -> return "Code parsed successfully with no diagnostics"
                else
                    let baseError =
                        if String.IsNullOrEmpty(response.Errors) then
                            "Code parsing failed"
                        else
                            $"Error parsing code: {response.Errors}"

                    let errorMessage = formatErrorWithDiagnostics baseError response
                    return errorMessage
            }

        [<McpServerTool; Description("Reset the F# Interactive session, clearing all bindings and state")>]
        static member ResetFSISession
            (
                fsiService: FsiMcpService,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! record = fsiService.ExecuteOperation(ResetSession, "", timeout = timeout)
                let result = record.Result

                if result.IsSuccess then
                    return "FSI session reset successfully"
                else
                    let baseError =
                        if String.IsNullOrEmpty(result.Errors) then
                            "Failed to reset FSI session"
                        else
                            $"Error resetting FSI session: {result.Errors}"

                    let errorMessage = formatErrorWithResult baseError result
                    return errorMessage
            }

        [<McpServerTool; Description("Get the current state and bindings in the F# Interactive session")>]
        static member GetFSIState
            (
                fsiService: FsiMcpService,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! record = fsiService.ExecuteOperation(GetState, "", timeout = timeout)
                let result = record.Result

                if result.IsSuccess then
                    return result.Output
                else
                    let baseError =
                        if String.IsNullOrEmpty(result.Errors) then
                            "Failed to get FSI state"
                        else
                            $"Error getting FSI state: {result.Errors}"

                    let errorMessage = formatErrorWithResult baseError result
                    return errorMessage
            }

        [<McpServerTool; Description("Check if the F# Interactive server is running and accessible")>]
        static member CheckFSIServerStatus(fsiService: FsiMcpService) : Task<string> =
            task {
                return
                    if fsiService.IsRunning then
                        "FSI server is running and accessible"
                    else
                        "FSI server is not accessible"
            }

    /// MCP Tools for Safe Code Injection - extending FSharpInteractiveTools
    [<McpServerToolType>]
    type CodeInjectionTools =

        [<McpServerTool; Description("Parse source code to analyze its structure before injection")>]
        static member ParseSourceToAST
            (fsiService: FsiMcpService, [<Description("F# source code to parse")>] sourceCode: string)
            : Task<string> =
            task {
                try
                    let client = fsiService.GetClient()
                    let! parseResult = client.ParseAndCheck(sourceCode)

                    if parseResult.IsSuccess then
                        match parseResult.Diagnostics with
                        | Some diagnostics ->
                            let errorCount =
                                diagnostics
                                |> Array.filter (fun d -> d.Severity.ToString() = "Error")
                                |> Array.length

                            let warningCount =
                                diagnostics
                                |> Array.filter (fun d -> d.Severity.ToString() = "Warning")
                                |> Array.length

                            let infoCount =
                                diagnostics
                                |> Array.filter (fun d -> d.Severity.ToString() = "Info")
                                |> Array.length

                            let summary =
                                sprintf
                                    "Parse successful. Errors: %d, Warnings: %d, Info: %d"
                                    errorCount
                                    warningCount
                                    infoCount

                            if diagnostics.Length > 0 then
                                let diagnosticDetails =
                                    diagnostics
                                    |> Array.map (fun d ->
                                        sprintf "%s at line %d: %s" (d.Severity.ToString()) d.StartLine d.Message)
                                    |> String.concat "\n"

                                return sprintf "%s\n\nDiagnostics:\n%s" summary diagnosticDetails
                            else
                                return summary
                        | None -> return "Parse successful with no diagnostics"
                    else
                        return sprintf "Parse failed: %s" parseResult.Errors
                with ex ->
                    return sprintf "Error parsing source code: %s" ex.Message
            }

        [<McpServerTool; Description("Analyze the structure of an existing F# script file")>]
        static member AnalyzeCodeStructure
            (fsiService: FsiMcpService, [<Description("Path to the F# script file to analyze")>] filePath: string)
            : Task<string> =
            task {
                try
                    let client = fsiService.GetClient()

                    if not (System.IO.File.Exists(filePath)) then
                        return sprintf "Error: File not found: %s" filePath
                    else
                        match validateFSharpFileType filePath with
                        | Error errorMsg -> return errorMsg
                        | Ok() ->
                            let sourceCode = System.IO.File.ReadAllText(filePath)
                            let! parseResult = client.ParseAndCheck(sourceCode)

                            if parseResult.IsSuccess then
                                let lineCount = sourceCode.Split([| '\n' |], StringSplitOptions.None).Length
                                let charCount = sourceCode.Length

                                let analysisResult =
                                    sprintf "File: %s\nLines: %d\nCharacters: %d\n" filePath lineCount charCount

                                match parseResult.Diagnostics with
                                | Some diagnostics ->
                                    let errorCount =
                                        diagnostics
                                        |> Array.filter (fun d -> d.Severity.ToString() = "Error")
                                        |> Array.length

                                    let warningCount =
                                        diagnostics
                                        |> Array.filter (fun d -> d.Severity.ToString() = "Warning")
                                        |> Array.length

                                    let diagnosticSummary = sprintf "Errors: %d, Warnings: %d" errorCount warningCount

                                    if diagnostics.Length > 0 then
                                        let topIssues =
                                            diagnostics
                                            |> Array.take (min 5 diagnostics.Length)
                                            |> Array.map (fun d ->
                                                sprintf
                                                    "  %s at line %d: %s"
                                                    (d.Severity.ToString())
                                                    d.StartLine
                                                    d.Message)
                                            |> String.concat "\n"

                                        let moreMsg =
                                            if diagnostics.Length > 5 then
                                                sprintf "\n  ... and %d more issues" (diagnostics.Length - 5)
                                            else
                                                ""

                                        return
                                            sprintf
                                                "%s%s\n\nTop Issues:\n%s%s"
                                                analysisResult
                                                diagnosticSummary
                                                topIssues
                                                moreMsg
                                    else
                                        return
                                            sprintf
                                                "%s%s\n\nNo issues found - file is ready for code injection."
                                                analysisResult
                                                diagnosticSummary
                                | None ->
                                    return
                                        sprintf
                                            "%sNo diagnostics available\n\nFile appears to be valid for code injection."
                                            analysisResult
                            else
                                return sprintf "Error analyzing file: %s" parseResult.Errors
                with ex ->
                    return sprintf "Error analyzing code structure: %s" ex.Message
            }

        [<McpServerTool;
          Description("Preview what the code will look like after injection without actually writing to file")>]
        static member PreviewCodeInjection
            (
                fsiService: FsiMcpService,
                [<Description("F# code to inject")>] newCode: string,
                [<Description("Path to the target script file")>] filePath: string,
                [<Description("Line number where to insert the code (1-based, optional)")>] ?insertAtLine: int,
                [<Description("Column position for indentation (1-based, optional)")>] ?insertAtColumn: int
            ) : Task<string> =
            task {
                try
                    // Validate file type first
                    let fileTypeValidation =
                        if System.IO.File.Exists(filePath) then
                            validateFSharpFileType filePath
                        else
                            // For new files, validate based on the provided path extension
                            validateFSharpFileType filePath

                    match fileTypeValidation with
                    | Error errorMsg -> return errorMsg
                    | Ok() ->

                        // Read the existing file content
                        let existingCode =
                            if System.IO.File.Exists(filePath) then
                                System.IO.File.ReadAllText(filePath)
                            else
                                ""

                        // Determine insertion point
                        let combinedCode =
                            match insertAtLine with
                            | Some lineNum ->
                                let lines = splitLinesPreservingLineEndings existingCode

                                if lineNum <= 0 || lineNum > lines.Length + 1 then
                                    sprintf "Error: Invalid line number %d. File has %d lines." lineNum lines.Length
                                else
                                    let newCodeLines = splitLinesPreservingLineEndings newCode

                                    // Apply column positioning if specified
                                    let indentedNewCodeLines =
                                        match insertAtColumn with
                                        | Some column when column > 1 ->
                                            let indent = String.replicate (column - 1) " "

                                            newCodeLines
                                            |> Array.mapi (fun i line ->
                                                if i = 0 then indent + line.TrimStart()
                                                else if String.IsNullOrWhiteSpace(line) then line
                                                else indent + line)
                                        | _ -> newCodeLines

                                    let beforeLines = lines |> Array.take (lineNum - 1)
                                    let afterLines = lines |> Array.skip (lineNum - 1)
                                    let allLines = Array.concat [ beforeLines; indentedNewCodeLines; afterLines ]
                                    joinLinesWithConsistentEndings allLines
                            | None ->
                                // FIXED: Append to end with proper spacing, matching InsertCodeUnified behavior
                                let combined =
                                    if String.IsNullOrWhiteSpace(existingCode) then
                                        newCode
                                    else
                                        existingCode.TrimEnd() + "\n\n" + newCode

                                combined

                        let previewTitle =
                            match insertAtLine, insertAtColumn with
                            | Some line, Some col ->
                                sprintf "Preview of code injection into %s at line %d, column %d:" filePath line col
                            | Some line, None -> sprintf "Preview of code injection into %s at line %d:" filePath line
                            | None, Some col ->
                                sprintf "Preview of code injection into %s (end of file, column %d):" filePath col
                            | None, None -> sprintf "Preview of code injection into %s:" filePath

                        return sprintf "%s\n\n%s" previewTitle combinedCode

                with ex ->
                    return sprintf "Error previewing code injection: %s" ex.Message
            }



        [<McpServerTool; Description("Format an entire F# script file using Fantomas")>]
        static member FormatFile
            (fsiService: FsiMcpService, [<Description("Path to the F# script file to format")>] filePath: string)
            : Task<string> =
            task {
                try
                    if not (System.IO.File.Exists(filePath)) then
                        return sprintf "Error: File not found: %s" filePath
                    else
                        match validateFSharpFileType filePath with
                        | Error errorMsg -> return errorMsg
                        | Ok() ->
                            let sourceCode = System.IO.File.ReadAllText(filePath)

                            try
                                let! formatResult =
                                    CodeFormatter.FormatDocumentAsync(filePath.EndsWith(".fsi"), sourceCode)

                                let formattedContent = formatResult.Code

                                if formattedContent <> sourceCode then
                                    System.IO.File.WriteAllText(filePath, formattedContent)
                                    return sprintf "File %s has been formatted by Fantomas" filePath
                                else
                                    return sprintf "File %s was already properly formatted" filePath
                            with ex ->
                                return sprintf "Error formatting file %s: %s" filePath ex.Message
                with ex ->
                    return sprintf "Error accessing file %s: %s" filePath ex.Message
            }

    /// MCP Build and Deployment Tools
    [<McpServerToolType>]
    type KillMCPServer =

        [<McpServerTool; Description("Kill all MCP server processes")>]
        static member KillAll(fsiService: FsiMcpService) : Task<string> =
            task {
                try
                    let mutable output = System.Text.StringBuilder()
                    output.AppendLine("=== Killing MCP Server Processes ===") |> ignore

                    let mutable totalKilled = 0

                    // Kill FSharp.MCP.DevKit processes
                    try
                        let processes = System.Diagnostics.Process.GetProcessesByName("FSharp.MCP.DevKit")

                        if processes.Length > 0 then
                            output.AppendLine($"\nFound {processes.Length} FSharp.MCP.DevKit process(es)")
                            |> ignore

                            for proc in processes do
                                try
                                    output.AppendLine($"   - Killing process ID {proc.Id}") |> ignore
                                    proc.Kill()
                                    proc.WaitForExit(5000) |> ignore
                                    totalKilled <- totalKilled + 1
                                    output.AppendLine($"   ✓ Successfully killed process ID {proc.Id}") |> ignore
                                with ex ->
                                    output.AppendLine($"   ✗ Failed to kill process ID {proc.Id}: {ex.Message}")
                                    |> ignore
                        else
                            output.AppendLine("\nNo FSharp.MCP.DevKit processes found") |> ignore

                        // Kill dotnet processes running FSharp.MCP.DevKit
                        let dotnetProcesses = System.Diagnostics.Process.GetProcessesByName("dotnet")
                        let mutable foundDotnetMcp = false

                        for proc in dotnetProcesses do
                            try
                                let cmdLine = proc.StartInfo.Arguments

                                if cmdLine.Contains("FSharp.MCP.DevKit") then
                                    if not foundDotnetMcp then
                                        output.AppendLine($"\nFound dotnet process(es) running FSharp.MCP.DevKit")
                                        |> ignore

                                        foundDotnetMcp <- true

                                    output.AppendLine($"   - Killing dotnet process ID {proc.Id}") |> ignore
                                    proc.Kill()
                                    proc.WaitForExit(5000) |> ignore
                                    totalKilled <- totalKilled + 1

                                    output.AppendLine($"   ✓ Successfully killed dotnet process ID {proc.Id}")
                                    |> ignore
                            with ex ->
                                output.AppendLine(
                                    $"   ✗ Error checking/killing dotnet process ID {proc.Id}: {ex.Message}"
                                )
                                |> ignore

                        if not foundDotnetMcp then
                            output.AppendLine("\nNo dotnet processes running FSharp.MCP.DevKit found")
                            |> ignore

                    with ex ->
                        output.AppendLine($"\nError during process cleanup: {ex.Message}") |> ignore

                    // Summary
                    output.AppendLine($"\n=== Summary ===") |> ignore

                    if totalKilled > 0 then
                        output.AppendLine($"✅ Successfully killed {totalKilled} MCP server process(es)")
                        |> ignore

                        output.AppendLine($"MCP server processes have been terminated.") |> ignore
                    else
                        output.AppendLine($"ℹ️  No MCP server processes were running") |> ignore

                    return output.ToString()

                with ex ->
                    return sprintf "Error during MCP server cleanup: %s" ex.Message
            }


        [<McpServerTool; Description("Delete specific lines from an F# file")>]
        static member DeleteLines
            (
                fsiService: FsiMcpService,
                [<Description("Path to the target F# file (.fsx, .fs, .fsi)")>] filePath: string,
                [<Description("Starting line number to delete (1-based)")>] startLine: int,
                [<Description("Ending line number to delete (1-based, inclusive)")>] endLine: int
            ) : Task<string> =
            task {
                try
                    if not (System.IO.File.Exists(filePath)) then
                        return sprintf "Error: File not found: %s" filePath
                    else
                        match validateFSharpFileType filePath with
                        | Error errorMsg -> return errorMsg
                        | Ok() ->

                            let lines = System.IO.File.ReadAllLines(filePath)
                            let totalLines = lines.Length

                            if startLine <= 0 || endLine <= 0 || startLine > totalLines || endLine > totalLines then
                                return
                                    sprintf
                                        "Error: Invalid line range %d-%d. File has %d lines."
                                        startLine
                                        endLine
                                        totalLines
                            elif startLine > endLine then
                                return
                                    sprintf "Error: Start line %d cannot be greater than end line %d." startLine endLine
                            else
                                let beforeLines = lines |> Array.take (startLine - 1)
                                let afterLines = lines |> Array.skip endLine
                                let newContent = Array.concat [ beforeLines; afterLines ]

                                System.IO.File.WriteAllLines(filePath, newContent)
                                let deletedCount = endLine - startLine + 1

                                return
                                    sprintf "Deleted %d lines (%d-%d) from %s" deletedCount startLine endLine filePath

                with ex ->
                    return sprintf "Error deleting lines: %s" ex.Message
            }

        [<McpServerTool; Description("Replace text in a specific line range with new content")>]
        static member ReplaceTextRange
            (
                fsiService: FsiMcpService,
                [<Description("Path to the target script file")>] filePath: string,
                [<Description("Starting line number to replace (1-based)")>] startLine: int,
                [<Description("Ending line number to replace (1-based, inclusive)")>] endLine: int,
                [<Description("New content to replace with")>] newContent: string
            ) : Task<string> =
            task {
                try
                    if not (System.IO.File.Exists(filePath)) then
                        return sprintf "Error: File not found: %s" filePath
                    else
                        let lines = System.IO.File.ReadAllLines(filePath)
                        let totalLines = lines.Length

                        if startLine <= 0 || endLine <= 0 || startLine > totalLines || endLine > totalLines then
                            return
                                sprintf
                                    "Error: Invalid line range %d-%d. File has %d lines."
                                    startLine
                                    endLine
                                    totalLines
                        elif startLine > endLine then
                            return sprintf "Error: Start line %d cannot be greater than end line %d." startLine endLine
                        else
                            let beforeLines = lines |> Array.take (startLine - 1)
                            let afterLines = lines |> Array.skip endLine

                            // FIXED: Preserve empty lines in new content by NOT using RemoveEmptyEntries
                            let newContentLines = splitLinesPreservingLineEndings newContent

                            let newLines = Array.concat [ beforeLines; newContentLines; afterLines ]

                            let finalContent = joinLinesWithConsistentEndings newLines
                            System.IO.File.WriteAllText(filePath, finalContent, System.Text.Encoding.UTF8)
                            let replacedCount = endLine - startLine + 1
                            let addedCount = newContentLines.Length

                            return
                                sprintf
                                    "Replaced %d lines (%d-%d) with %d lines in %s"
                                    replacedCount
                                    startLine
                                    endLine
                                    addedCount
                                    filePath

                with ex ->
                    return sprintf "Error replacing text range: %s" ex.Message
            }

        [<McpServerTool; Description("Search and replace text patterns in a file using string replacement")>]
        static member SearchAndReplace
            (
                fsiService: FsiMcpService,
                [<Description("Path to the target script file")>] filePath: string,
                [<Description("Text pattern to search for")>] searchPattern: string,
                [<Description("Replacement text")>] replacement: string,
                [<Description("Replace all occurrences (default: true)")>] ?replaceAll: bool
            ) : Task<string> =
            task {
                try
                    if not (System.IO.File.Exists(filePath)) then
                        return sprintf "Error: File not found: %s" filePath
                    else
                        let content = System.IO.File.ReadAllText(filePath)
                        let shouldReplaceAll = defaultArg replaceAll true

                        let newContent, replacementCount =
                            if shouldReplaceAll then
                                // Count occurrences before replacing
                                let mutable count = 0
                                let mutable pos = 0

                                while pos < content.Length do
                                    let index = content.IndexOf(searchPattern, pos)

                                    if index >= 0 then
                                        count <- count + 1
                                        pos <- index + searchPattern.Length
                                    else
                                        pos <- content.Length

                                let newContent = content.Replace(searchPattern, replacement)
                                newContent, count
                            else if content.Contains(searchPattern) then
                                let index = content.IndexOf(searchPattern)
                                let before = content.Substring(0, index)
                                let after = content.Substring(index + searchPattern.Length)
                                before + replacement + after, 1
                            else
                                content, 0

                        if replacementCount > 0 then
                            System.IO.File.WriteAllText(filePath, newContent)
                            let mode = if shouldReplaceAll then "all" else "first"

                            return
                                sprintf
                                    "Replaced %d occurrence(s) of '%s' with '%s' (%s mode) in %s"
                                    replacementCount
                                    searchPattern
                                    replacement
                                    mode
                                    filePath
                        else
                            return sprintf "No occurrences of '%s' found in %s" searchPattern filePath

                with ex ->
                    return sprintf "Error during search and replace: %s" ex.Message
            }

        [<McpServerTool; Description("Move a block of code from one location to another within a file")>]
        static member MoveCodeBlock
            (
                fsiService: FsiMcpService,
                [<Description("Path to the target script file")>] filePath: string,
                [<Description("Starting line of code block to move (1-based)")>] fromStartLine: int,
                [<Description("Ending line of code block to move (1-based, inclusive)")>] fromEndLine: int,
                [<Description("Target line number where to insert the moved block (1-based)")>] targetLine: int
            ) : Task<string> =
            task {
                try
                    if not (System.IO.File.Exists(filePath)) then
                        return sprintf "Error: File not found: %s" filePath
                    else
                        let lines = System.IO.File.ReadAllLines(filePath)
                        let totalLines = lines.Length

                        if
                            fromStartLine <= 0
                            || fromEndLine <= 0
                            || fromStartLine > totalLines
                            || fromEndLine > totalLines
                        then
                            return
                                sprintf
                                    "Error: Invalid source range %d-%d. File has %d lines."
                                    fromStartLine
                                    fromEndLine
                                    totalLines
                        elif fromStartLine > fromEndLine then
                            return
                                sprintf
                                    "Error: Start line %d cannot be greater than end line %d."
                                    fromStartLine
                                    fromEndLine
                        elif targetLine <= 0 || targetLine > totalLines + 1 then
                            return sprintf "Error: Invalid target line %d. File has %d lines." targetLine totalLines
                        else
                            // Extract the code block to move
                            let codeBlock =
                                lines
                                |> Array.skip (fromStartLine - 1)
                                |> Array.take (fromEndLine - fromStartLine + 1)

                            // Remove the code block from original position
                            let beforeBlock = lines |> Array.take (fromStartLine - 1)
                            let afterBlock = lines |> Array.skip fromEndLine
                            let linesWithoutBlock = Array.concat [ beforeBlock; afterBlock ]

                            // Adjust target line if it's after the removed block
                            let adjustedTargetLine =
                                if targetLine > fromEndLine then
                                    targetLine - (fromEndLine - fromStartLine + 1)
                                elif targetLine > fromStartLine then
                                    fromStartLine
                                else
                                    targetLine

                            // Insert the code block at the target position
                            let beforeTarget = linesWithoutBlock |> Array.take (adjustedTargetLine - 1)
                            let afterTarget = linesWithoutBlock |> Array.skip (adjustedTargetLine - 1)
                            let finalLines = Array.concat [ beforeTarget; codeBlock; afterTarget ]

                            System.IO.File.WriteAllLines(filePath, finalLines)
                            let blockSize = fromEndLine - fromStartLine + 1

                            return
                                sprintf
                                    "Moved %d lines from %d-%d to line %d in %s"
                                    blockSize
                                    fromStartLine
                                    fromEndLine
                                    adjustedTargetLine
                                    filePath

                with ex ->
                    return sprintf "Error moving code block: %s" ex.Message
            }

        [<McpServerTool; Description("Get specific lines from a file for inspection")>]
        static member GetLines
            (
                fsiService: FsiMcpService,
                [<Description("Path to the target script file")>] filePath: string,
                [<Description("Starting line number (1-based)")>] startLine: int,
                [<Description("Ending line number (1-based, inclusive, optional)")>] ?endLine: int
            ) : Task<string> =
            task {
                try
                    if not (System.IO.File.Exists(filePath)) then
                        return sprintf "Error: File not found: %s" filePath
                    else
                        let lines = System.IO.File.ReadAllLines(filePath)
                        let totalLines = lines.Length
                        let actualEndLine = defaultArg endLine startLine

                        if
                            startLine <= 0
                            || actualEndLine <= 0
                            || startLine > totalLines
                            || actualEndLine > totalLines
                        then
                            return
                                sprintf
                                    "Error: Invalid line range %d-%d. File has %d lines."
                                    startLine
                                    actualEndLine
                                    totalLines
                        elif startLine > actualEndLine then
                            return
                                sprintf
                                    "Error: Start line %d cannot be greater than end line %d."
                                    startLine
                                    actualEndLine
                        else
                            let selectedLines =
                                lines
                                |> Array.skip (startLine - 1)
                                |> Array.take (actualEndLine - startLine + 1)

                            let numberedLines =
                                selectedLines
                                |> Array.mapi (fun i line -> sprintf "%d: %s" (startLine + i) line)

                            let result = String.Join("\n", numberedLines)
                            return sprintf "Lines %d-%d from %s:\n%s" startLine actualEndLine filePath result

                with ex ->
                    return sprintf "Error getting lines: %s" ex.Message
            }

        [<McpServerTool; Description("Count total lines in a file")>]
        static member CountLines
            (fsiService: FsiMcpService, [<Description("Path to the target script file")>] filePath: string)
            : Task<string> =
            task {
                try
                    if not (System.IO.File.Exists(filePath)) then
                        return sprintf "Error: File not found: %s" filePath
                    else
                        let lines = System.IO.File.ReadAllLines(filePath)
                        let totalLines = lines.Length
                        let totalChars = System.IO.File.ReadAllText(filePath).Length
                        return sprintf "File %s has %d lines and %d characters" filePath totalLines totalChars

                with ex ->
                    return sprintf "Error counting lines: %s" ex.Message
            }

        [<McpServerTool; Description("Search for text patterns in an F# file and return line numbers")>]
        static member SearchInFile
            (
                fsiService: FsiMcpService,
                [<Description("Path to the target F# file (.fsx, .fs, .fsi)")>] filePath: string,
                [<Description("Text pattern to search for")>] searchPattern: string,
                [<Description("Case sensitive search (default: false)")>] ?caseSensitive: bool
            ) : Task<string> =
            task {
                try
                    if not (System.IO.File.Exists(filePath)) then
                        return sprintf "Error: File not found: %s" filePath
                    else
                        match validateFSharpFileType filePath with
                        | Error errorMsg -> return errorMsg
                        | Ok() ->

                            let lines = System.IO.File.ReadAllLines(filePath)
                            let isCaseSensitive = defaultArg caseSensitive false

                            let comparison =
                                if isCaseSensitive then
                                    StringComparison.Ordinal
                                else
                                    StringComparison.OrdinalIgnoreCase

                            let matches =
                                lines
                                |> Array.mapi (fun i line -> (i + 1, line))
                                |> Array.filter (fun (_, line) -> line.Contains(searchPattern, comparison))

                            let limitedMatches =
                                if matches.Length > 20 then
                                    matches |> Array.take 20
                                else
                                    matches

                            if limitedMatches.Length > 0 then
                                let matchStrings =
                                    limitedMatches
                                    |> Array.map (fun (lineNum, line) -> sprintf "%d: %s" lineNum (line.Trim()))

                                let result = String.Join("\n", matchStrings)

                                let moreMsg =
                                    if matches.Length > 20 then
                                        "\n... (showing first 20 matches)"
                                    else
                                        ""

                                return
                                    sprintf
                                        "Found %d occurrence(s) of '%s' in %s (showing first %d)%s\n%s"
                                        matches.Length
                                        searchPattern
                                        filePath
                                        limitedMatches.Length
                                        moreMsg
                                        result
                            else
                                return sprintf "No occurrences of '%s' found in %s" searchPattern filePath

                with ex ->
                    return sprintf "Error searching in file: %s" ex.Message
            }

        [<McpServerTool>]
        [<Description("Unified code insertion with pre-format, post-format, and atomic write operations for F# files. Validation is optional and disabled by default to handle large code pieces better.")>]
        static member InsertCode
            (
                fsiService: FsiMcpService,
                [<Description("F# code to insert")>] newCode: string,
                [<Description("Path to the target F# file (.fsx, .fs, .fsi)")>] filePath: string,
                [<Description("Line number where to insert the code (1-based)")>] insertAtLine: int,
                [<Description("Column position for indentation (1-based, optional - if not provided, preserves existing indentation)")>] insertAtColumn:
                    int,
                [<Description("Whether to format the code (default: true)")>] ?shouldFormat: bool,
                [<Description("Whether to validate the code before insertion (default: false, since validation can fail with large code pieces)")>] ?shouldValidate:
                    bool
            ) : Task<string> =
            task {
                try
                    let shouldFormat = defaultArg shouldFormat true
                    let shouldValidate = defaultArg shouldValidate false
                    let client = fsiService.GetClient()

                    // Validate file type first
                    match validateFSharpFileType filePath with
                    | Error errorMsg -> return errorMsg
                    | Ok() ->

                        // Step 1: Read existing file content
                        let existingCode =
                            if System.IO.File.Exists(filePath) then
                                System.IO.File.ReadAllText(filePath)
                            else
                                ""

                        // Step 2: Pre-format new code if requested
                        let! preformattedNewCode =
                            if shouldFormat then
                                task {
                                    try
                                        let! formatResult = CodeFormatter.FormatDocumentAsync(false, newCode)
                                        return formatResult.Code
                                    with _ ->
                                        return newCode // Fallback to original if formatting fails
                                }
                            else
                                Task.FromResult(newCode)

                        // Step 3: Validate and combine code safely with optional column positioning
                        let validateAndCombine () =
                            let lines = splitLinesPreservingLineEndings existingCode

                            if insertAtLine <= 0 || insertAtLine > lines.Length + 1 then
                                Error(
                                    sprintf
                                        "Error: Invalid line number %d. File has %d lines."
                                        insertAtLine
                                        lines.Length
                                )
                            else
                                // Split the new code into lines
                                let newCodeLines = splitLinesPreservingLineEndings preformattedNewCode

                                // Apply column positioning/indentation if specified
                                let indentedNewCodeLines =
                                    if insertAtColumn > 1 then
                                        let indent = String.replicate (insertAtColumn - 1) " "

                                        newCodeLines
                                        |> Array.mapi (fun i line ->
                                            if i = 0 then
                                                // First line: preserve any existing indentation in the new code
                                                indent + line.TrimStart()
                                            else if
                                                // Subsequent lines: add the base indent but preserve relative indentation
                                                String.IsNullOrWhiteSpace(line)
                                            then
                                                line // Keep empty lines as-is
                                            else
                                                indent + line)
                                    else
                                        newCodeLines // No column specified, use original indentation

                                // Validate insertion context (but don't fail on it, just warn)
                                let contextWarning =
                                    match validateInsertionContext lines insertAtLine indentedNewCodeLines with
                                    | Error msg -> Some msg
                                    | Ok() -> None

                                let beforeLines = lines |> Array.take (insertAtLine - 1)
                                let afterLines = lines |> Array.skip (insertAtLine - 1)
                                let allLines = Array.concat [ beforeLines; indentedNewCodeLines; afterLines ]
                                let combinedCode = joinLinesWithConsistentEndings allLines

                                Ok(combinedCode, contextWarning)

                        let combinedResult = validateAndCombine ()

                        match combinedResult with
                        | Error errorMsg -> return errorMsg
                        | Ok(combinedCode, contextWarning) ->
                            // Step 4: Validate combined code if requested (but don't fail, just report)
                            let! validationResult =
                                if shouldValidate then
                                    task {
                                        let! parseResult = client.ParseAndCheck(combinedCode)

                                        if not parseResult.IsSuccess then
                                            return sprintf " (validation failed: %s)" parseResult.Errors
                                        else
                                            match parseResult.Diagnostics with
                                            | Some diagnostics ->
                                                let errors =
                                                    diagnostics
                                                    |> Array.filter (fun d -> d.Severity.ToString() = "Error")

                                                let warnings =
                                                    diagnostics
                                                    |> Array.filter (fun d -> d.Severity.ToString() = "Warning")

                                                let infos =
                                                    diagnostics
                                                    |> Array.filter (fun d -> d.Severity.ToString() = "Info")

                                                // Build diagnostic summary (but don't fail on errors)
                                                let parts =
                                                    [ if errors.Length > 0 then
                                                          yield sprintf "%d error(s)" errors.Length
                                                      if warnings.Length > 0 then
                                                          yield sprintf "%d warning(s)" warnings.Length
                                                      if infos.Length > 0 then
                                                          yield sprintf "%d info message(s)" infos.Length ]

                                                if parts.Length > 0 then
                                                    return sprintf " (found %s)" (String.concat ", " parts)
                                                else
                                                    return ""
                                            | None -> return ""
                                    }
                                else
                                    Task.FromResult("")

                            // Combine context warning and validation summary
                            let allWarnings =
                                [ match contextWarning with
                                  | Some warning -> yield sprintf " (context warning: %s)" warning
                                  | None -> ()

                                  if not (String.IsNullOrEmpty(validationResult)) then
                                      yield validationResult ]

                            let combinedDiagnostics = String.concat "" allWarnings

                            // Step 5: Format entire document if requested
                            let! finalCode =
                                if shouldFormat then
                                    task {
                                        try
                                            let! formatResult =
                                                CodeFormatter.FormatDocumentAsync(
                                                    filePath.EndsWith(".fsi"),
                                                    combinedCode
                                                )

                                            return formatResult.Code
                                        with _ ->
                                            return combinedCode // Fallback to unformatted if document formatting fails
                                    }
                                else
                                    Task.FromResult(combinedCode)

                            // Step 6: Atomic write operation with backup
                            let writeFileSafely () =
                                try
                                    let backupPath = filePath + ".backup"
                                    let tempPath = filePath + ".tmp"

                                    // Create backup if file exists
                                    if System.IO.File.Exists(filePath) then
                                        System.IO.File.Copy(filePath, backupPath, true)

                                    // Write to temp file
                                    System.IO.File.WriteAllText(tempPath, finalCode)

                                    // Atomic move
                                    if System.IO.File.Exists(filePath) then
                                        System.IO.File.Delete(filePath)

                                    System.IO.File.Move(tempPath, filePath)

                                    // Clean up backup
                                    if System.IO.File.Exists(backupPath) then
                                        System.IO.File.Delete(backupPath)

                                    Ok()
                                with ex ->
                                    Error ex.Message

                            match writeFileSafely () with
                            | Ok() ->
                                let locationMsg = sprintf "line %d" insertAtLine

                                let columnMsg =
                                    if insertAtColumn > 1 then
                                        sprintf " at column %d" insertAtColumn
                                    else
                                        ""

                                let formatMsg = if shouldFormat then " and formatted" else ""

                                let validationMsg =
                                    if shouldValidate then
                                        " (validated)"
                                    else
                                        " (validation skipped)"

                                return
                                    sprintf
                                        "Code successfully inserted%s into %s at %s%s%s%s"
                                        formatMsg
                                        filePath
                                        locationMsg
                                        columnMsg
                                        validationMsg
                                        combinedDiagnostics
                            | Error errorMsg -> return sprintf "Failed to write file: %s" errorMsg

                with ex ->
                    return sprintf "Error during unified code insertion: %s" ex.Message
            }

        [<McpServerTool; Description("Restart the F# Interactive session (stop and start fresh, better than reset)")>]
        static member RestartFSISession
            (
                fsiService: FsiMcpService,
                [<Description("Timeout in seconds (optional, default: 30)")>] ?timeoutSeconds: int
            ) : Task<string> =
            task {
                let timeout =
                    match timeoutSeconds with
                    | Some seconds -> TimeSpan.FromSeconds(float seconds)
                    | None -> fsiService.DefaultTimeout

                let! record = fsiService.ExecuteOperation(RestartHost, "", timeout = timeout)
                let result = record.Result

                if result.IsSuccess then
                    return "FSI session restarted successfully"
                else
                    let baseError =
                        if String.IsNullOrEmpty(result.Errors) then
                            "Failed to restart FSI session"
                        else
                            $"Error restarting FSI session: {result.Errors}"

                    let errorMessage = formatErrorWithResult baseError result
                    return errorMessage
            }

        [<McpServerTool; Description("Get all symbols in F# source code with detailed information")>]
        static member GetAllSymbols
            (fsiService: FsiMcpService, [<Description("F# source code to analyze for symbols")>] sourceCode: string)
            : Task<string> =
            task {
                try
                    let symbolService = SmartSymbolDetection.createSymbolDetectionService ()

                    match symbolService.GetAllSymbols(sourceCode) with
                    | Ok symbols ->
                        let symbolCount = symbols.Length

                        let symbolsByKind =
                            symbols
                            |> Array.groupBy (fun s -> s.SymbolKind)
                            |> Array.map (fun (kind, syms) ->
                                let uniqueNames = syms |> Array.map (fun s -> s.Name) |> Array.distinct
                                sprintf "%s (%d): %s" kind syms.Length (String.concat ", " uniqueNames))
                            |> String.concat "\n"

                        let detailedSymbols =
                            symbols
                            |> Array.map (fun sym ->
                                sprintf
                                    "  %s (%s) at (%d,%d)-(%d,%d)\n    Full name: %s\n    Signature: %s"
                                    sym.Name
                                    sym.SymbolKind
                                    sym.StartLine
                                    sym.StartColumn
                                    sym.EndLine
                                    sym.EndColumn
                                    (sym.FullTypeName |> Option.defaultValue "None")
                                    (sym.Signature |> Option.defaultValue "None"))
                            |> String.concat "\n\n"

                        return
                            sprintf
                                "Found %d symbols:\n\nBy kind:\n%s\n\nDetailed list:\n%s"
                                symbolCount
                                symbolsByKind
                                detailedSymbols
                    | Error msg -> return sprintf "Error analyzing symbols: %s" msg
                with ex ->
                    return sprintf "Exception in symbol analysis: %s" ex.Message
            }

        [<McpServerTool; Description("Find symbol at a specific position in F# source code")>]
        static member GetSymbolAtPosition
            (
                fsiService: FsiMcpService,
                [<Description("F# source code to analyze")>] sourceCode: string,
                [<Description("Line number (1-based)")>] lineNumber: int,
                [<Description("Column number (1-based)")>] columnNumber: int
            ) : Task<string> =
            task {
                try
                    let symbolService = SmartSymbolDetection.createSymbolDetectionService ()

                    match symbolService.GetSymbolAtPosition(sourceCode, lineNumber, columnNumber) with
                    | Ok symbol ->
                        return
                            sprintf
                                "Symbol at line %d, column %d:\n  Name: %s\n  Kind: %s\n  Full name: %s\n  Signature: %s\n  Range: (%d,%d) to (%d,%d)\n  Documentation: %s"
                                lineNumber
                                columnNumber
                                symbol.Name
                                symbol.SymbolKind
                                (symbol.FullTypeName |> Option.defaultValue "None")
                                (symbol.Signature |> Option.defaultValue "None")
                                symbol.StartLine
                                symbol.StartColumn
                                symbol.EndLine
                                symbol.EndColumn
                                (symbol.Documentation |> Option.defaultValue "None")
                    | Error msg ->
                        return sprintf "No symbol found at line %d, column %d: %s" lineNumber columnNumber msg
                with ex ->
                    return sprintf "Exception finding symbol at position: %s" ex.Message
            }

        [<McpServerTool; Description("Get a quick description of what symbol is at a specific position")>]
        static member WhatIsAtPosition
            (
                fsiService: FsiMcpService,
                [<Description("F# source code to analyze")>] sourceCode: string,
                [<Description("Line number (1-based)")>] lineNumber: int,
                [<Description("Column number (1-based)")>] columnNumber: int
            ) : Task<string> =
            task {
                try
                    let symbolService = SmartSymbolDetection.createSymbolDetectionService ()
                    let result = symbolService.WhatIsAt(sourceCode, lineNumber, columnNumber)
                    return sprintf "At line %d, column %d: %s" lineNumber columnNumber result
                with ex ->
                    return sprintf "Exception: %s" ex.Message
            }

        [<McpServerTool; Description("Get the signature of a symbol at a specific position")>]
        static member GetSymbolSignatureAtPosition
            (
                fsiService: FsiMcpService,
                [<Description("F# source code to analyze")>] sourceCode: string,
                [<Description("Line number (1-based)")>] lineNumber: int,
                [<Description("Column number (1-based)")>] columnNumber: int
            ) : Task<string> =
            task {
                try
                    let symbolService = SmartSymbolDetection.createSymbolDetectionService ()

                    match symbolService.GetSignatureAtPosition(sourceCode, lineNumber, columnNumber) with
                    | Ok signature ->
                        return sprintf "Signature at line %d, column %d: %s" lineNumber columnNumber signature
                    | Error msg ->
                        return sprintf "No signature available at line %d, column %d: %s" lineNumber columnNumber msg
                with ex ->
                    return sprintf "Exception getting signature: %s" ex.Message
            }
