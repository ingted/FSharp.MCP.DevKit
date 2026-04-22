module McpExecutionToolsTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open Akka.FSI.Contracts
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.Integration
open FSharp.MCP.DevKit.Server.McpFsiTools

let private waitForCompletion (service: FsiMcpService) asyncId =
    task {
        let mutable attempt = 0
        let mutable status = service.GetAsyncExecutionStatus(asyncId)

        while not status.IsCompleted && attempt < 50 do
            do! Task.Delay(100)
            attempt <- attempt + 1
            status <- service.GetAsyncExecutionStatus(asyncId)

        return status
    }

type private BrowserExecutionResponse =
    { ResultId: string
      RequestId: string
      HostId: string
      SessionId: string
      BrowserId: string
      TabId: string option
      IsSuccess: bool
      Output: string
      Errors: string
      Metadata: Map<string, string> }

type private ScheduledExecutionDto =
    { ScheduleId: string
      AgentId: string
      HostId: string
      SessionId: string
      OperationKind: string
      DueAtUtc: DateTime
      CreatedAtUtc: DateTime
      StartedAtUtc: DateTime option
      CompletedAtUtc: DateTime option
      Status: string
      ResultId: string option
      RetryCount: int
      LastError: string option
      Metadata: Map<string, string> }

type private ScheduledExecutionProcessDto =
    { Processed: bool
      Item: ScheduledExecutionDto option
      ResultId: string option
      IsSuccess: bool option
      Output: string option
      Errors: string option }

type private ScheduledExecutionBatchDto =
    { ProcessedCount: int
      Items: ScheduledExecutionProcessDto list }

type private FakeProcSupervisorClient(startFactory: string * ProcHostSpec -> ProcHostSnapshot, healthFactory: string -> ProcHostSnapshot option) =
    interface IProcSupervisorClient with
        member _.StartProc(procId: string, spec: ProcHostSpec) = Task.FromResult(startFactory (procId, spec))
        member _.StopProc(_, _) = Task.FromException<ProcHostSnapshot>(InvalidOperationException("Not used"))
        member _.GetProcInfo(procId: string) = Task.FromResult(healthFactory procId)
        member _.ListProcInfo() = Task.FromResult([])
        member _.RestartProc(procId: string) =
            match healthFactory procId with
            | Some value -> Task.FromResult(value)
            | None -> Task.FromException<ProcHostSnapshot>(InvalidOperationException("Missing proc"))

type private FakeFsiSupervisorClient() =
    interface IFsiSupervisorClient with
        member _.Execute(_host: HostRecord, request: FsiSupervisorExecRequest) =
            Task.FromResult(
                { SessionId = request.SessionId
                  RawErrorType = None
                  Result =
                    { Output = "remote supervisor accepted: " + request.Code
                      Errors = ""
                      IsSuccess = true
                      ExecutionTime = Some(TimeSpan.FromMilliseconds 5.0)
                      Diagnostics = [||]
                      Value = Some request.Code } }
            )

        member _.GetSessionInfo(_host: HostRecord, sessionId: string) =
            Task.FromResult(
                { SessionId = sessionId
                  Status = "ready"
                  Refs = []
                  Loads = []
                  SearchPaths = []
                  Variables = []
                  LastCheckpointId = None
                  RunningSinceUtc = Some DateTime.UtcNow }
            )

        member _.ListSessions(_) =
            Task.FromResult(
                [ { SessionId = "agent-self-session"
                    Status = "ready"
                    Refs = []
                    Loads = []
                    SearchPaths = []
                    Variables = []
                    LastCheckpointId = None
                    RunningSinceUtc = Some DateTime.UtcNow } ]
            )

        member _.EnsureSession(_, sessionId: string) =
            Task.FromResult(
                { SessionId = sessionId
                  Existed = false
                  Status = "created" }
            )

        member _.ResetSession(_, sessionId: string) =
            Task.FromResult(
                { SessionId = sessionId
                  Existed = true
                  Status = "reset" }
            )

let private isolatedService () =
    let root =
        Path.Combine(
            Path.GetTempPath(),
            "fsharp-devkit-mcp-execution-tools-tests",
            Guid.NewGuid().ToString("N")
        )

    Directory.CreateDirectory(root) |> ignore

    let outputSubscriberBroker = InMemoryOutputSubscriberBroker() :> IOutputSubscriberBroker
    let sessionOutputLiveStore = JsonLineSessionOutputLiveStore(root) :> ISessionOutputLiveStore
    let outputStore = SessionOutputStore(outputSubscriberBroker, sessionOutputLiveStore) :> IOutputStore
    let sessionOutputArchiveStore = JsonLineSessionOutputArchiveStore(root) :> ISessionOutputArchiveStore
    let executionStore = JsonLineResultRegistry(root) :> IExecutionStore
    let queue = ScheduledExecutionQueue(root)

    let service =
        new FsiMcpService(
            NullLogger<FsiMcpService>.Instance,
            enableRemoteClient = false,
            outputSubscriberBroker = outputSubscriberBroker,
            sessionOutputLiveStore = sessionOutputLiveStore,
            outputStore = outputStore,
            sessionOutputArchiveStore = sessionOutputArchiveStore,
            executionStore = executionStore,
            scheduledExecutionQueue = queue
        )

    let cleanup =
        { new IDisposable with
            member _.Dispose() =
                (service :> IDisposable).Dispose()

                try
                    Directory.Delete(root, true)
                with _ ->
                    () }

    service, cleanup

let private remoteFabricService () =
    let root =
        Path.Combine(
            Path.GetTempPath(),
            "fsharp-devkit-agent-self-fabric-tests",
            Guid.NewGuid().ToString("N")
        )

    Directory.CreateDirectory(root) |> ignore

    let hostSpec =
        { ExecutablePath = "dotnet"
          Arguments = [ "fsi-host.dll" ]
          WorkingDirectory = Some root
          Role = Some "procnode"
          ProbeMessage = Some "PING"
          ProbeCron = None
          ProbeIntervalMs = Some 1000 }

    let procSnapshot procId spec =
        { ProcId = procId
          Status = "running"
          ProcessId = Some 9911
          FsiSupervisorPath = Some "akka://agent-self-fabric/user/fsi/supervisor"
          NodeAddress = Some "akka://agent-self-fabric"
          LastProbeUtc = Some DateTime.UtcNow
          LastProbeOk = Some true
          ProbeFailures = 0
          Spec = spec
          LastError = None }

    let procClient =
        FakeProcSupervisorClient(
            (fun (procId, spec) -> procSnapshot procId (Some spec)),
            (fun procId -> Some(procSnapshot procId None))
        )

    let outputSubscriberBroker = InMemoryOutputSubscriberBroker() :> IOutputSubscriberBroker
    let sessionOutputLiveStore = JsonLineSessionOutputLiveStore(root) :> ISessionOutputLiveStore
    let outputStore = SessionOutputStore(outputSubscriberBroker, sessionOutputLiveStore) :> IOutputStore
    let sessionOutputArchiveStore = JsonLineSessionOutputArchiveStore(root) :> ISessionOutputArchiveStore
    let executionStore = JsonLineResultRegistry(root) :> IExecutionStore
    let queue = ScheduledExecutionQueue(root)

    let service =
        new FsiMcpService(
            NullLogger<FsiMcpService>.Instance,
            enableRemoteClient = false,
            procSupervisorClient = (procClient :> IProcSupervisorClient),
            fsiSupervisorClient = (FakeFsiSupervisorClient() :> IFsiSupervisorClient),
            outputSubscriberBroker = outputSubscriberBroker,
            sessionOutputLiveStore = sessionOutputLiveStore,
            outputStore = outputStore,
            sessionOutputArchiveStore = sessionOutputArchiveStore,
            executionStore = executionStore,
            scheduledExecutionQueue = queue
        )

    let cleanup =
        { new IDisposable with
            member _.Dispose() =
                (service :> IDisposable).Dispose()

                try
                    Directory.Delete(root, true)
                with _ ->
                    () }

    service, cleanup, hostSpec

let private createMgmt2DirectEnvelopeJson executionId requestId output =
    let timestamp = DateTimeOffset.Parse("2026-04-22T15:10:00Z")
    let envelope: WinAgentEnvelopeImport.WinAgentSharedExecutionEnvelope =
        { SchemaVersion = 1
          ExecutionPlane = "mgmt2-direct-proc-supervisor"
          ExecutionId = executionId
          RequestId = requestId
          ToolName = "Mgmt2.RemoteFsi.DirectProcSupervisor"
          RouteName = "agent-self-procnode/agent-self-session"
          Status = "succeeded"
          StartedAtUtc = timestamp
          CompletedAtUtc = timestamp.AddSeconds(1.0)
          Output = output
          Error = None
          ExceptionType = None
          Metadata =
            Map.ofList
                [ "execution.source", "Mgmt2.DirectProcSupervisor"
                  "execution.plane", "remote-fsi"
                  "principal.id", "human-direct"
                  "principal.kind", "human"
                  "principal.source", "mgmt2" ]
          OutputEvents =
            [ { SequenceNo = 1L
                StreamKind = "stdout"
                Text = output
                IsReplay = false
                TimestampUtc = timestamp.AddSeconds(1.0) } ] }

    JsonSerializer.Serialize(envelope, WinAgentEnvelopeImport.jsonOptions)

[<Fact>]
let ``McpExecutionTools browser-aware routed execution records schedule target metadata`` () =
    task {
        let service, cleanup = isolatedService()
        use _cleanup = cleanup

        let! _ =
            service.ExecuteOperation(
                FSharp.MCP.DevKit.Core.ExecuteCode,
                "let browserScheduleBootstrap = 1",
                timeout = TimeSpan.FromSeconds 30.0
            )

        let! responseJson =
            McpExecutionTools.ExecuteBrowserFSharpCodeRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "browser-01",
                "let browserScheduleValue = 123\nprintfn \"browser scheduled\"",
                "tab-02",
                "tab",
                "",
                "",
                "remote-fsi",
                30,
                "human-browser",
                "human",
                "mgmt2"
            )

        let response = FSharpJson.deserialize<BrowserExecutionResponse> responseJson
        let stored = service.TryGetResult(response.ResultId) |> Option.get

        Assert.True(response.IsSuccess)
        Assert.Equal("default-host", response.HostId)
        Assert.Equal("default-session", response.SessionId)
        Assert.Equal("browser-01", response.BrowserId)
        Assert.Equal(Some "tab-02", response.TabId)
        Assert.Contains("browserScheduleValue", response.Output)
        Assert.Equal("browser-01", response.Metadata.["browser.id"])
        Assert.Equal("tab-02", response.Metadata.["browser.tabId"])
        Assert.Equal("default-session", response.Metadata.["browser.companion.sessionId"])
        Assert.Equal("human-browser", response.Metadata.[PrincipalAttribution.PrincipalId])
        Assert.Equal("human", response.Metadata.[PrincipalAttribution.PrincipalKind])
        Assert.Equal("mgmt2", response.Metadata.[PrincipalAttribution.PrincipalSource])
        Assert.Equal("browser-01", stored.Metadata.["browser.id"])
        Assert.Equal("tab-02", stored.Metadata.["schedule.target.tabId"])
        Assert.Equal("human-browser", stored.Metadata.[PrincipalAttribution.PrincipalId])
    }

[<Fact>]
let ``McpExecutionTools execute evaluate reset and async on explicit default route work`` () =
    task {
        let service, cleanup = isolatedService()
        use _cleanup = cleanup
        let tempPath = Path.GetTempPath()

        let! _ = service.ExecuteOperation(FSharp.MCP.DevKit.Core.ExecuteCode, "let routedBootstrap = 40", timeout = TimeSpan.FromSeconds 30.0)

        let! execOutput =
            McpExecutionTools.ExecuteFSharpCodeRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "let routedExplicit = 77",
                30,
                "human-mgmt2",
                "human",
                "mgmt2"
            )

        let execRecord =
            service.ListAgentResults("default-agent")
            |> List.find (fun record ->
                record.Metadata.TryFind PrincipalAttribution.PrincipalId
                |> Option.exists ((=) "human-mgmt2"))

        let! evalOutput =
            McpExecutionTools.EvaluateFSharpExpressionRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "routedExplicit",
                30,
                "codex-cli",
                "agent",
                "mcp"
            )

        let! addPathOutput =
            McpExecutionTools.AddSearchPathRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                tempPath,
                30
            )

        let! stateOutput =
            McpExecutionTools.GetFsiStateRouted(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let! asyncId =
            McpExecutionTools.ExecuteFSharpCodeAsyncRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "let routedAsyncValue = 88",
                30,
                "codex-cli",
                "agent",
                "mcp"
            )

        let! asyncStatus = waitForCompletion service asyncId

        let! resetOutput =
            McpExecutionTools.ResetFsiSessionRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                30
            )

        let! postResetEval =
            McpExecutionTools.EvaluateFSharpExpressionRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "routedExplicit",
                30
            )

        Assert.Contains("routedExplicit", execOutput)
        Assert.Equal("human-mgmt2", execRecord.Metadata.[PrincipalAttribution.PrincipalId])
        Assert.Equal("human", execRecord.Metadata.[PrincipalAttribution.PrincipalKind])
        Assert.Equal("mgmt2", execRecord.Metadata.[PrincipalAttribution.PrincipalSource])
        Assert.Equal("77", evalOutput)
        Assert.Equal($"Search path added successfully: {tempPath}", addPathOutput)
        Assert.Contains("FSI Session State", stateOutput)
        Assert.Contains("SessionId: default-session", stateOutput)
        Assert.True(asyncStatus.Exists)
        Assert.True(asyncStatus.IsCompleted)
        Assert.Equal(Some "default-agent", asyncStatus.AgentId)
        Assert.Equal(Some "default-host", asyncStatus.HostId)
        Assert.Equal(Some "default-session", asyncStatus.SessionId)
        match asyncStatus.ResultId with
        | Some resultId ->
            let asyncRecord = service.TryGetResult(resultId) |> Option.get
            Assert.Equal("codex-cli", asyncRecord.Metadata.[PrincipalAttribution.PrincipalId])
            Assert.Equal("agent", asyncRecord.Metadata.[PrincipalAttribution.PrincipalKind])
            Assert.Equal("mcp", asyncRecord.Metadata.[PrincipalAttribution.PrincipalSource])
        | None -> Assert.Fail("Expected routed async execution to store a result id.")
        Assert.Equal("FSI session reset successfully", resetOutput)
        Assert.Contains("Expression evaluation failed", postResetEval)
    }

[<Fact>]
let ``Agent self fabric colocates agent Mgmt2 editor direct and scheduled executions on one remote session`` () =
    task {
        let service, cleanup, hostSpec = remoteFabricService()
        use _cleanup = cleanup

        let agentId = "PulseTrade.Management2"
        let hostId = "agent-self-procnode"
        let sessionId = "agent-self-session"

        service.RegisterAgent(agentId, "Mgmt2/Agent self fabric") |> ignore
        let! _ = service.CreateHost(agentId, Net10Host, hostSpec, requestedHostId = hostId)
        let! _ = service.CreateSession(agentId, hostId, sessionId = sessionId)

        let! codexText =
            McpExecutionTools.ExecuteFSharpCodeRouted(
                service,
                agentId,
                hostId,
                sessionId,
                "printfn \"fabric-codex-tool\"; 101",
                30,
                "codex-cli",
                "agent",
                "mcp"
            )

        let! editorText =
            McpExecutionTools.ExecuteFSharpCodeRouted(
                service,
                agentId,
                hostId,
                sessionId,
                "printfn \"fabric-mgmt2-editor\"; 202",
                30,
                "human-editor",
                "human",
                "mgmt2"
            )

        let directEnvelopeJson =
            createMgmt2DirectEnvelopeJson
                "agent-self-direct-1"
                "agent-self-direct-req-1"
                "fabric-mgmt2-remote-window"

        let _ =
            McpResultTools.ImportWinAgentExecutionEnvelope(
                service,
                agentId,
                hostId,
                sessionId,
                directEnvelopeJson
            )

        let! _ =
            McpExecutionTools.ScheduleFSharpCodeRouted(
                service,
                agentId,
                hostId,
                sessionId,
                "printfn \"fabric-scheduled\"; 303",
                "",
                30,
                "codex-scheduler",
                "agent",
                "mcp"
            )

        let! processedJson = McpExecutionTools.ProcessDueScheduledFsiExecutionBatch(service, 10)
        let! fabricJson = McpResultTools.ListExecutionFabricRecordsByHostSession(service, hostId, sessionId, 20)

        let outputJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                agentId,
                hostId,
                sessionId,
                0L,
                0
            )

        let! codexPrincipalJson = McpResultTools.ListExecutionFabricRecordsByPrincipalId(service, "codex-cli", 20)
        let! humanPrincipalJson = McpResultTools.ListExecutionFabricRecordsByPrincipalId(service, "human-editor", 20)

        let processed = FSharpJson.deserialize<ScheduledExecutionBatchDto> processedJson
        let records = FSharpJson.deserialize<ExecutionFabricRecord list> fabricJson
        let events = FSharpJson.deserialize<OutputEventRecord list> outputJson

        Assert.Contains("fabric-codex-tool", codexText)
        Assert.Contains("fabric-mgmt2-editor", editorText)
        Assert.Equal(1, processed.ProcessedCount)
        Assert.True(records |> List.length >= 4)
        Assert.True(records |> List.forall (fun record -> record.target.hostId = hostId && record.target.sessionId = sessionId))
        Assert.True(events |> List.forall (fun eventRecord -> eventRecord.SessionId = sessionId))
        Assert.Contains("fabric-codex-tool", fabricJson)
        Assert.Contains("fabric-mgmt2-editor", fabricJson)
        Assert.Contains("fabric-mgmt2-remote-window", fabricJson)
        Assert.Contains("fabric-scheduled", fabricJson)
        Assert.Contains("fabric-codex-tool", outputJson)
        Assert.Contains("fabric-mgmt2-editor", outputJson)
        Assert.Contains("fabric-mgmt2-remote-window", outputJson)
        Assert.Contains("fabric-scheduled", outputJson)
        Assert.Contains("codex-cli", codexPrincipalJson)
        Assert.Contains("human-editor", humanPrincipalJson)
    }

[<Fact>]
let ``McpExecutionTools schedule routed FSI execution processes due item into result fabric`` () =
    task {
        let service, cleanup = isolatedService()
        use _cleanup = cleanup

        let! _ =
            service.ExecuteOperation(
                FSharp.MCP.DevKit.Core.ExecuteCode,
                "let schedulerBootstrap = 1",
                timeout = TimeSpan.FromSeconds 30.0
            )

        let futureDue = DateTime.UtcNow.AddMinutes(5.0).ToString("O")

        let! futureJson =
            McpExecutionTools.ScheduleFSharpCodeRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "let shouldNotRunYet = 1",
                futureDue,
                30,
                "codex-cli",
                "agent",
                "mcp"
            )

        let futureItem = FSharpJson.deserialize<ScheduledExecutionDto> futureJson

        let! notDueJson = McpExecutionTools.ProcessNextDueScheduledFsiExecution(service)
        let notDue = FSharpJson.deserialize<ScheduledExecutionProcessDto> notDueJson

        let! dueJson =
            McpExecutionTools.ScheduleFSharpCodeRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "let scheduledValue = 144",
                "",
                30,
                "human-scheduler",
                "human",
                "mgmt2"
            )

        let dueItem = FSharpJson.deserialize<ScheduledExecutionDto> dueJson
        let! pendingJson = McpExecutionTools.ListScheduledFsiExecutions(service, "default-agent", "default-host", "default-session", "pending")
        let pendingItems = FSharpJson.deserialize<ScheduledExecutionDto list> pendingJson

        let! processedJson = McpExecutionTools.ProcessDueScheduledFsiExecutionBatch(service, 10)
        let processed = FSharpJson.deserialize<ScheduledExecutionBatchDto> processedJson

        let completed = processed.Items |> List.find (fun item -> item.Item.Value.ScheduleId = dueItem.ScheduleId)
        let stored = service.TryGetResult(completed.ResultId.Value) |> Option.get

        Assert.Equal("pending", futureItem.Status)
        Assert.False(notDue.Processed)
        Assert.Contains(pendingItems, fun item -> item.ScheduleId = futureItem.ScheduleId)
        Assert.Contains(pendingItems, fun item -> item.ScheduleId = dueItem.ScheduleId)
        Assert.Equal(1, processed.ProcessedCount)
        Assert.True(completed.Processed)
        Assert.Equal("completed", completed.Item.Value.Status)
        Assert.Equal(Some true, completed.IsSuccess)
        Assert.Contains("scheduledValue", completed.Output.Value)
        Assert.Equal(dueItem.ScheduleId, stored.Metadata.["schedule.id"])
        Assert.Equal("fsi-code", stored.Metadata.["schedule.kind"])
        Assert.Equal("human-scheduler", stored.Metadata.[PrincipalAttribution.PrincipalId])
        Assert.Equal("human", stored.Metadata.[PrincipalAttribution.PrincipalKind])
        Assert.Equal("mgmt2", stored.Metadata.[PrincipalAttribution.PrincipalSource])
    }

[<Fact>]
let ``McpExecutionTools scheduled browser execution preserves target metadata`` () =
    task {
        let service, cleanup = isolatedService()
        use _cleanup = cleanup

        let! _ =
            service.ExecuteOperation(
                FSharp.MCP.DevKit.Core.ExecuteCode,
                "let browserSchedulerBootstrap = 1",
                timeout = TimeSpan.FromSeconds 30.0
            )

        let! scheduledJson =
            McpExecutionTools.ScheduleBrowserFSharpCodeRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "browser-scheduled-01",
                "let browserScheduledValue = 233\nprintfn \"browser scheduled due\"",
                "",
                "tab-scheduled-02",
                "tab",
                "",
                "",
                "winagent-shared-fsi-host",
                30,
                "human-browser-scheduler",
                "human",
                "mgmt2"
            )

        let scheduled = FSharpJson.deserialize<ScheduledExecutionDto> scheduledJson
        let! processedJson = McpExecutionTools.ProcessNextDueScheduledFsiExecution(service)
        let processed = FSharpJson.deserialize<ScheduledExecutionProcessDto> processedJson
        let stored = service.TryGetResult(processed.ResultId.Value) |> Option.get

        Assert.Equal("pending", scheduled.Status)
        Assert.Equal("browser-fsi-code", scheduled.Metadata.["schedule.kind"])
        Assert.Equal("browser-scheduled-01", scheduled.Metadata.["schedule.target.browserId"])
        Assert.Equal("tab-scheduled-02", scheduled.Metadata.["schedule.target.tabId"])
        Assert.Equal("default-host", scheduled.Metadata.["schedule.target.companion.hostId"])
        Assert.Equal("default-session", scheduled.Metadata.["schedule.target.companion.sessionId"])
        Assert.True(processed.Processed)
        Assert.Equal(Some true, processed.IsSuccess)
        Assert.Contains("browserScheduledValue", processed.Output.Value)
        Assert.Equal(scheduled.ScheduleId, stored.Metadata.["schedule.id"])
        Assert.Equal("browser-fsi-code", stored.Metadata.["schedule.kind"])
        Assert.Equal("browser-scheduled-01", stored.Metadata.["browser.id"])
        Assert.Equal("tab-scheduled-02", stored.Metadata.["browser.tabId"])
        Assert.Equal("human-browser-scheduler", stored.Metadata.[PrincipalAttribution.PrincipalId])
        Assert.Equal("human", stored.Metadata.[PrincipalAttribution.PrincipalKind])
        Assert.Equal("mgmt2", stored.Metadata.[PrincipalAttribution.PrincipalSource])
    }

[<Fact>]
let ``McpExecutionTools scheduled execution supports cancel and failed requeue`` () =
    task {
        let service, cleanup = isolatedService()
        use _cleanup = cleanup

        let! _ =
            service.ExecuteOperation(
                FSharp.MCP.DevKit.Core.ExecuteCode,
                "let schedulerControlBootstrap = 1",
                timeout = TimeSpan.FromSeconds 30.0
            )

        let futureDue = DateTime.UtcNow.AddMinutes(5.0).ToString("O")

        let! cancellableJson =
            McpExecutionTools.ScheduleFSharpCodeRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "let cancelValue = 1",
                futureDue,
                30
            )

        let cancellable = FSharpJson.deserialize<ScheduledExecutionDto> cancellableJson
        let! cancelledJson = McpExecutionTools.CancelScheduledFsiExecution(service, cancellable.ScheduleId, "manual-test")
        let cancelled = FSharpJson.deserialize<ScheduledExecutionDto> cancelledJson

        let! failedSourceJson =
            McpExecutionTools.ScheduleFSharpCodeRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "this scheduled symbol does not exist",
                "",
                30
            )

        let failedSource = FSharpJson.deserialize<ScheduledExecutionDto> failedSourceJson
        let! failedProcessJson = McpExecutionTools.ProcessNextDueScheduledFsiExecution(service)
        let failedProcess = FSharpJson.deserialize<ScheduledExecutionProcessDto> failedProcessJson

        let requeueDue = DateTime.UtcNow.AddMinutes(10.0).ToString("O")
        let! requeuedJson = McpExecutionTools.RequeueFailedScheduledFsiExecution(service, failedSource.ScheduleId, requeueDue)
        let requeued = FSharpJson.deserialize<ScheduledExecutionDto> requeuedJson

        let! cancelledListJson = McpExecutionTools.ListScheduledFsiExecutions(service, "", "", "", "cancelled")
        let cancelledItems = FSharpJson.deserialize<ScheduledExecutionDto list> cancelledListJson

        Assert.Equal("cancelled", cancelled.Status)
        Assert.Equal(Some "manual-test", cancelled.LastError)
        Assert.Contains(cancelledItems, fun item -> item.ScheduleId = cancellable.ScheduleId)
        Assert.True(failedProcess.Processed)
        Assert.Equal(Some false, failedProcess.IsSuccess)
        Assert.Equal("failed", failedProcess.Item.Value.Status)
        Assert.Equal(failedSource.ScheduleId, failedProcess.Item.Value.ScheduleId)
        Assert.True(failedProcess.Item.Value.ResultId.IsSome)
        Assert.Equal("pending", requeued.Status)
        Assert.Equal(1, requeued.RetryCount)
        Assert.True(requeued.DueAtUtc > DateTime.UtcNow.AddMinutes(5.0))
        Assert.True(requeued.ResultId.IsNone)
        Assert.True(requeued.LastError.IsNone)
    }

[<Fact>]
let ``McpExecutionTools scheduled execution requeue with backoff computes next due`` () =
    task {
        let service, cleanup = isolatedService()
        use _cleanup = cleanup

        let! _ =
            service.ExecuteOperation(
                FSharp.MCP.DevKit.Core.ExecuteCode,
                "let schedulerBackoffBootstrap = 1",
                timeout = TimeSpan.FromSeconds 30.0
            )

        let! failedSourceJson =
            McpExecutionTools.ScheduleFSharpCodeRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "this backoff symbol does not exist",
                "",
                30
            )

        let failedSource = FSharpJson.deserialize<ScheduledExecutionDto> failedSourceJson
        let! failedProcessJson = McpExecutionTools.ProcessNextDueScheduledFsiExecution(service)
        let failedProcess = FSharpJson.deserialize<ScheduledExecutionProcessDto> failedProcessJson
        let beforeRequeue = DateTime.UtcNow

        let! requeuedJson =
            McpExecutionTools.RequeueFailedScheduledFsiExecutionWithBackoff(
                service,
                failedSource.ScheduleId,
                60,
                300
            )

        let requeued = FSharpJson.deserialize<ScheduledExecutionDto> requeuedJson

        Assert.Equal("failed", failedProcess.Item.Value.Status)
        Assert.Equal("pending", requeued.Status)
        Assert.Equal(1, requeued.RetryCount)
        Assert.True(requeued.DueAtUtc >= beforeRequeue.AddSeconds(55.0))
        Assert.True(requeued.DueAtUtc <= beforeRequeue.AddSeconds(75.0))
        Assert.True(requeued.ResultId.IsNone)
        Assert.True(requeued.LastError.IsNone)
    }

[<Fact>]
let ``ScheduledExecutionQueue persists latest item state for reload`` () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "devkit-scheduler-" + Guid.NewGuid().ToString("N"))

    try
        let route =
            { AgentId = "agent-persist"
              HostId = "host-persist"
              SessionId = "session-persist" }

        let queue = ScheduledExecutionQueue(tempRoot)

        let item =
            queue.Enqueue(
                route,
                ExecuteCode,
                "let persistedSchedule = 1",
                DateTime.UtcNow.AddSeconds(-1.0),
                Some(TimeSpan.FromSeconds 30.0),
                Map.ofList [ "schedule.kind", "fsi-code" ]
            )

        let running = queue.TryStartNextDue(DateTime.UtcNow) |> Option.get
        let failed = queue.Fail(running.ScheduleId, "boom", resultId = "result-persist-001")
        let reloaded = ScheduledExecutionQueue(tempRoot)
        let loaded = reloaded.TryGet(item.ScheduleId) |> Option.get

        let journalPath = Path.Combine(tempRoot, "scheduled", "queue.jsonl")

        Assert.Equal(item.ScheduleId, failed.ScheduleId)
        Assert.True(File.Exists journalPath)
        Assert.Equal(ScheduledFailed, loaded.Status)
        Assert.Equal(Some "result-persist-001", loaded.ResultId)
        Assert.Equal(Some "boom", loaded.LastError)
        Assert.Equal(route, loaded.Route)
    finally
        if Directory.Exists tempRoot then
            Directory.Delete(tempRoot, true)

[<Fact>]
let ``get_async_status can observe routed async completion without resources`` () =
    task {
        let service, cleanup = isolatedService()
        use _cleanup = cleanup

        let! _ =
            service.ExecuteOperation(
                FSharp.MCP.DevKit.Core.ExecuteCode,
                "let routedAsyncBootstrap = 1",
                timeout = TimeSpan.FromSeconds 30.0
            )

        let! asyncId =
            McpExecutionTools.ExecuteFSharpCodeAsyncRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "let routedAsyncProbe = 99",
                30
            )

        let mutable attempt = 0
        let! initial = FSharpInteractiveTools.GetAsyncStatus(service, asyncId)
        let mutable status = FSharpJson.deserialize<AsyncFsiStatusDto> initial

        while not status.IsCompleted && attempt < 50 do
            do! Task.Delay(100)
            attempt <- attempt + 1
            let! next = FSharpInteractiveTools.GetAsyncStatus(service, asyncId)
            status <- FSharpJson.deserialize<AsyncFsiStatusDto> next

        Assert.True(status.Exists)
        Assert.True(status.IsCompleted)
        Assert.Equal(Some "default-agent", status.AgentId)
        Assert.Equal(Some "default-host", status.HostId)
        Assert.Equal(Some "default-session", status.SessionId)
        Assert.True(status.Result.IsSome)
        Assert.True(status.Result.Value.IsSuccess)
    }

[<Fact>]
let ``McpExecutionTools returns actionable error when session is already faulted`` () =
    task {
        let service, cleanup = isolatedService()
        use _cleanup = cleanup

        let! failedRecord =
            service.ExecuteOperation(
                FSharp.MCP.DevKit.Core.ExecuteCode,
                "this symbol does not exist",
                timeout = TimeSpan.FromSeconds 30.0
            )

        let! evalOutput =
            McpExecutionTools.EvaluateFSharpExpressionRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "1 + 1",
                30
            )

        Assert.False(failedRecord.Result.IsSuccess)
        Assert.Contains("Faulted state", evalOutput)
        Assert.Contains("reset_fsi_session_routed", evalOutput)
        Assert.Contains(failedRecord.ResultId, evalOutput)
    }
