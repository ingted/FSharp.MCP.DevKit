namespace FSharp.MCP.DevKit.Server.Backends

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core

type private InProcSessionHandle =
    { Route: ExecutionRoute
      Service: FsiService
      Gate: SemaphoreSlim
      CreatedAt: DateTime
      mutable Status: SessionStatus
      mutable Refs: string list
      mutable Loads: string list
      mutable SearchPaths: string list
      mutable LastExecutionAt: DateTime option }

type InProcBackend(?config: FsiConfig) as this =
    let sessionHandles = ConcurrentDictionary<string, InProcSessionHandle>()
    let fsiConfig = defaultArg config FsiConfig.defaultConfig

    let sessionKey (route: ExecutionRoute) = $"{route.HostId}::{route.SessionId}"

    let createHandle (route: ExecutionRoute) =
        let service = FsiService(fsiConfig)
        service.Start()

        { Route = route
          Service = service
          Gate = new SemaphoreSlim(1, 1)
          CreatedAt = DateTime.UtcNow
          Status = SessionReady
          Refs = []
          Loads = []
          SearchPaths = []
          LastExecutionAt = None }

    let getOrCreateHandle (route: ExecutionRoute) =
        sessionHandles.GetOrAdd(sessionKey route, (fun _ -> createHandle route))

    let updateMetadata (handle: InProcSessionHandle) (request: ExecutionRequest) (result: FsiResult) (completedAt: DateTime) =
        handle.LastExecutionAt <- Some completedAt

        handle.Status <-
            if result.IsSuccess then
                SessionReady
            else
                SessionFaulted

        if result.IsSuccess then
            match request.OperationKind with
            | ReferenceAssembly
            | ReferenceNuget -> handle.Refs <- request.Payload :: handle.Refs
            | LoadScript -> handle.Loads <- request.Payload :: handle.Loads
            | AddSearchPath -> handle.SearchPaths <- request.Payload :: handle.SearchPaths
            | _ -> ()

    let toSessionRecord (handle: InProcSessionHandle) =
        { SessionId = handle.Route.SessionId
          AgentId = handle.Route.AgentId
          HostId = handle.Route.HostId
          SessionName = handle.Route.SessionId
          Status = handle.Status
          Refs = List.rev handle.Refs
          Loads = List.rev handle.Loads
          SearchPaths = List.rev handle.SearchPaths
          Variables = []
          LastCheckpointId = None
          RunningSinceUtc = Some handle.CreatedAt
          LastExecutionAt = handle.LastExecutionAt }

    let executeWithTimeout (timeout: TimeSpan option) (operation: unit -> Task<FsiResult>) =
        task {
            try
                match timeout with
                | None ->
                    let! result = operation ()
                    return (result, None)
                | Some value ->
                    let operationTask = operation ()
                    let! completedTask = Task.WhenAny(operationTask :> Task, Task.Delay(value))

                    if obj.ReferenceEquals(completedTask, operationTask :> Task) then
                        let! result = operationTask
                        return (result, None)
                    else
                        return
                            (BackendAdapters.createFailedResult
                                $"Operation timed out after {value.TotalSeconds} seconds."
                                None
                                (Some "TimeoutException"),
                             Some "TimeoutException")
            with ex ->
                return
                    (BackendAdapters.createFailedResult ex.Message None (Some(ex.GetType().FullName)),
                     Some(ex.GetType().FullName))
        }

    let executeOperation (handle: InProcSessionHandle) (request: ExecutionRequest) =
        match request.OperationKind with
        | ExecuteCode ->
            executeWithTimeout request.Timeout (fun () ->
                handle.Service.ExecuteInteractionAsync(request.Payload, CancellationToken.None))
        | EvaluateExpression ->
            executeWithTimeout request.Timeout (fun () -> task { return handle.Service.EvaluateExpression(request.Payload) })
        | LoadScript ->
            executeWithTimeout request.Timeout (fun () ->
                handle.Service.ExecuteScript(request.Payload, CancellationToken.None))
        | ReferenceAssembly ->
            executeWithTimeout request.Timeout (fun () -> task { return handle.Service.ReferenceAssembly(request.Payload) })
        | ReferenceNuget ->
            executeWithTimeout request.Timeout (fun () ->
                task { return handle.Service.ReferenceNugetPackage(request.Payload, ?usePackageTargets = request.UsePackageTargets) })
        | AddSearchPath ->
            executeWithTimeout request.Timeout (fun () -> task { return handle.Service.AddSearchPath(request.Payload) })
        | GetState ->
            executeWithTimeout request.Timeout (fun () ->
                task {
                    return
                        { FsiResult.empty with
                            Output = handle.Service.GetState()
                            Value = Some handle.Route.SessionId }
                })
        | ResetSession ->
            executeWithTimeout request.Timeout (fun () ->
                task {
                    handle.Service.Reset()

                    return
                        { FsiResult.empty with
                            Output = "FSI session reset"
                            Value = Some handle.Route.SessionId }
                })
        | RestartHost ->
            executeWithTimeout request.Timeout (fun () ->
                task {
                    handle.Service.Restart()

                    return
                        { FsiResult.empty with
                            Output = "In-proc session restarted"
                            Value = Some handle.Route.SessionId }
                })
        | ResultQuery ->
            task {
                return
                    (BackendAdapters.createFailedResult
                        "ResultQuery is not supported by InProcBackend directly."
                        None
                        (Some "UnsupportedOperationException"),
                     Some "UnsupportedOperationException")
            }

    interface IFsiExecutionBackend with
        member _.BackendKind = InProc

        member _.Execute(request: ExecutionRequest) =
            task {
                let submittedAt = DateTime.UtcNow
                let startedAt = Some DateTime.UtcNow
                let handle = getOrCreateHandle request.Route

                do! handle.Gate.WaitAsync()

                try
                    let! result, rawErrorType = executeOperation handle request
                    let completedAt = DateTime.UtcNow
                    updateMetadata handle request result completedAt

                    return
                        BackendAdapters.toExecutionRecord
                            InProc
                            request
                            submittedAt
                            startedAt
                            (Some completedAt)
                            request.Route.HostId
                            request.Route.SessionId
                            (Guid.NewGuid().ToString("N"))
                            result
                            rawErrorType
                finally
                    handle.Gate.Release() |> ignore
            }

        member _.GetSessionState(route: ExecutionRoute) =
            task {
                let handle = getOrCreateHandle route
                return toSessionRecord handle
            }

        member _.EnsureSession(route: ExecutionRoute) =
            task {
                let handle = getOrCreateHandle route
                return toSessionRecord handle
            }

        member _.ResetSession(route: ExecutionRoute) =
            task {
                let request =
                    { RequestId = Guid.NewGuid().ToString("N")
                      Route = route
                      OperationKind = ResetSession
                      Payload = ""
                      Timeout = Some(TimeSpan.FromSeconds 30.0)
                      UsePackageTargets = None
                      Metadata = Map.empty }

                return! (this :> IFsiExecutionBackend).Execute(request)
            }

        member _.RestartHost(host: HostRecord) =
            task {
                let matchingHandles =
                    sessionHandles.Values
                    |> Seq.filter (fun handle -> handle.Route.HostId = host.HostId)
                    |> Seq.toArray

                for handle in matchingHandles do
                    do! handle.Gate.WaitAsync()

                    try
                        handle.Service.Restart()
                        handle.Status <- SessionReady
                        handle.LastExecutionAt <- Some DateTime.UtcNow
                    finally
                        handle.Gate.Release() |> ignore
            }

        member _.HealthCheck(host: HostRecord) =
            task {
                return
                    { BackendKind = InProc
                      IsAvailable = true
                      Message = Some "In-proc backend available"
                      HostId = Some host.HostId
                      CheckedAt = DateTime.UtcNow }
            }
