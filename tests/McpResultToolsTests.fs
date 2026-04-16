module McpResultToolsTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.Integration
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Server.ResultQuery

type private FailOnceArchiveStore(inner: ISessionOutputArchiveStore) =
    let mutable shouldFail = true

    interface ISessionOutputArchiveStore with
        member _.Seal(sessionId: string, events: OutputEventRecord list, archivedAt: DateTime) =
            if shouldFail then
                shouldFail <- false
                raise (InvalidOperationException("tool seal failure"))
            else
                inner.Seal(sessionId, events, archivedAt)

        member _.ListEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            inner.ListEvents(sessionId, ?afterSequenceNo = afterSequenceNo, ?limit = limit)

        member _.ListArchives(?limit: int) =
            inner.ListArchives(?limit = limit)

        member _.TryGetArchive(sessionId: string) = inner.TryGetArchive(sessionId)

        member _.MarkSealPending(sessionId: string, events: OutputEventRecord list, pendingAt: DateTime, errorMessage: string) =
            inner.MarkSealPending(sessionId, events, pendingAt, errorMessage)

        member _.ListPendingEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            inner.ListPendingEvents(sessionId, ?afterSequenceNo = afterSequenceNo, ?limit = limit)

        member _.TryGetSealPending(sessionId: string) = inner.TryGetSealPending(sessionId)

        member _.RecoverSealPending(sessionId: string) = inner.RecoverSealPending(sessionId)

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

type private FakeFsiSupervisorClient(sessionFactory: HostRecord * string -> FsiSupervisorSessionSnapshot) =
    interface IFsiSupervisorClient with
        member _.Execute(host: HostRecord, request: FsiSupervisorExecRequest) =
            Task.FromResult(
                { SessionId = request.SessionId
                  RawErrorType = None
                  Result =
                    { Output = request.Code
                      Errors = ""
                      IsSuccess = true
                      ExecutionTime = Some(TimeSpan.FromMilliseconds 5.0)
                      Diagnostics = [||]
                      Value = None } }
            )

        member _.GetSessionInfo(host: HostRecord, sessionId: string) =
            Task.FromResult(sessionFactory (host, sessionId))

        member _.ListSessions(_) = Task.FromResult([])

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

[<Fact>]
let ``McpResultTools get list query compare and resources work`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! _ = service.ExecuteOperation(ExecuteCode, "let resultQueryValue = 10", timeout = TimeSpan.FromSeconds 30.0)
        let! first = service.ExecuteOperation(EvaluateExpression, "resultQueryValue", timeout = TimeSpan.FromSeconds 30.0)
        let! _ = service.ExecuteOperation(ExecuteCode, "let resultQueryValue = 11", timeout = TimeSpan.FromSeconds 30.0)
        let! second = service.ExecuteOperation(EvaluateExpression, "resultQueryValue", timeout = TimeSpan.FromSeconds 30.0)

        let singleJson = McpResultTools.GetFsiResult(service, "default-agent", first.ResultId)
        let listJson = McpResultTools.ListFsiResults(service, "default-agent", "", "")

        let mapJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "map",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "value",
                "",
                ""
            )

        let compareJson =
            McpResultTools.CompareFsiResults(
                service,
                "default-agent",
                first.ResultId,
                second.ResultId,
                "value",
                ""
            )

        let fsharpJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "map",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "records1 |> Seq.map (fun record -> record.Result.Value |> Option.defaultValue \"\") |> Seq.toList",
                "fsharpCode",
                ""
            )

        let filterMaterializedJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "filter",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "isSuccess",
                "",
                "syntheticResult"
            )

        let resultResource = ResultResources(service)
        let resultResourceJson = resultResource.Result(first.ResultId)
        let agentResultsJson = resultResource.AgentResults("default-agent")
        let sessionResultsJson = resultResource.SessionResults("default-host", "default-session")
        let sessionIdResultsJson = resultResource.SessionResultsBySessionId("default-session")
        let sessionIdToolJson = McpResultTools.ListFsiResultsBySessionId(service, "default-session")

        let single = FSharpJson.deserialize<FsiExecutionRecord option> singleJson
        let listed = FSharpJson.deserialize<FsiExecutionRecord list> listJson
        let sessionIdListed = FSharpJson.deserialize<FsiExecutionRecord list> sessionIdToolJson
        let mapResponse = FSharpJson.deserialize<ResultQueryResponse> mapJson
        let compareResponse = FSharpJson.deserialize<ResultQueryResponse> compareJson
        let fsharpResponse = FSharpJson.deserialize<ResultQueryResponse> fsharpJson
        let materializedResponse = FSharpJson.deserialize<ResultQueryResponse> filterMaterializedJson
        let synthetic = materializedResponse.ProducedResultIds |> List.head |> fun resultId -> service.TryGetResult(resultId)

        Assert.True(single.IsSome)
        Assert.Equal(first.ResultId, single.Value.ResultId)
        Assert.True(listed |> List.exists (fun value -> value.ResultId = first.ResultId))
        Assert.True(listed |> List.exists (fun value -> value.ResultId = second.ResultId))
        Assert.True(mapResponse.IsSuccess)
        Assert.Equal("[\"10\",\"11\"]", mapResponse.MaterializedJson.Value)
        Assert.True(compareResponse.IsSuccess)
        Assert.True(compareResponse.MaterializedJson.IsSome)
        Assert.Contains("\"leftValue\":\"10\"", compareResponse.MaterializedJson.Value)
        Assert.Contains("\"rightValue\":\"11\"", compareResponse.MaterializedJson.Value)
        Assert.True(fsharpResponse.IsSuccess)
        Assert.Equal("[\"10\",\"11\"]", fsharpResponse.MaterializedJson.Value)
        Assert.True(materializedResponse.IsSuccess)
        Assert.Single(materializedResponse.ProducedResultIds) |> ignore
        Assert.True(synthetic.IsSome)
        Assert.Equal(ResultQuery, synthetic.Value.OperationKind)
        Assert.Contains(first.ResultId, resultResourceJson)
        Assert.Contains(first.ResultId, agentResultsJson)
        Assert.Contains(second.ResultId, sessionResultsJson)
        Assert.Contains(second.ResultId, sessionIdResultsJson)
        Assert.True(sessionIdListed |> List.exists (fun value -> value.ResultId = second.ResultId))
    }

[<Fact>]
let ``McpResultTools output tools and resources expose live broker state`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore
            )

        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()

        let subscribeJson =
            McpResultTools.SubscribeSessionOutput(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "mgmt2-reader",
                0L,
                true
            )

        let _ = service.PublishSessionOutput("stdout", "alpha", executionId = "exec-out-1")
        let _ = service.PublishSessionOutput("stderr", "beta", executionId = "exec-out-1")

        let subscribersJson =
            McpResultTools.ListSessionOutputSubscribers(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let outputJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                0L,
                0
            )

        let outputAfterJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                1L,
                0
            )

        let resultResource = ResultResources(service)
        let outputResourceJson = resultResource.SessionOutput("default-host", "default-session")
        let outputAfterResourceJson = resultResource.SessionOutputAfter("default-host", "default-session", 1L)
        let subscribersResourceJson = resultResource.SessionOutputSubscribers("default-host", "default-session")

        let subscribed = FSharpJson.deserialize<OutputSubscriberRecord> subscribeJson
        let subscribers = FSharpJson.deserialize<OutputSubscriberRecord list> subscribersJson
        let events = FSharpJson.deserialize<OutputEventRecord list> outputJson
        let eventsAfter = FSharpJson.deserialize<OutputEventRecord list> outputAfterJson
        let resourceEvents = FSharpJson.deserialize<OutputEventRecord list> outputResourceJson
        let resourceEventsAfter = FSharpJson.deserialize<OutputEventRecord list> outputAfterResourceJson
        let resourceSubscribers = FSharpJson.deserialize<OutputSubscriberRecord list> subscribersResourceJson

        Assert.Equal("mgmt2-reader", subscribed.SubscriberId)
        Assert.Single(subscribers) |> ignore
        Assert.Equal(2, events.Length)
        Assert.Equal(1L, events[0].SequenceNo)
        Assert.Equal(2L, events[1].SequenceNo)
        Assert.Single(eventsAfter) |> ignore
        Assert.Equal("beta", eventsAfter[0].Payload)
        Assert.Equal(events.Length, resourceEvents.Length)
        Assert.Single(resourceEventsAfter) |> ignore
        Assert.Single(resourceSubscribers) |> ignore
        let unsubscribeJson =
            McpResultTools.UnsubscribeSessionOutput(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "mgmt2-reader"
            )

        let unsubscribed = FSharpJson.deserialize<bool> unsubscribeJson
        Assert.True(unsubscribed)
    }

[<Fact>]
let ``McpResultTools session output resources keep same read path after archive seal`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore
            )

        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()

        let _ = service.PublishSessionOutput("stdout", "archived-alpha", executionId = "exec-archive-2")
        let _ = service.PublishSessionOutput("stderr", "archived-beta", executionId = "exec-archive-2")
        let archive =
            match service.SealSessionOutputArchive() with
            | Archived value -> value
            | SealPending pending -> failwithf "expected archived outcome but got pending: %s" pending.ErrorMessage

        let outputJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                0L,
                0
            )

        let resultResource = ResultResources(service)
        let outputResourceJson = resultResource.SessionOutput("default-host", "default-session")
        let outputAfterResourceJson = resultResource.SessionOutputAfter("default-host", "default-session", 1L)

        let events = FSharpJson.deserialize<OutputEventRecord list> outputJson
        let resourceEvents = FSharpJson.deserialize<OutputEventRecord list> outputResourceJson
        let resourceEventsAfter = FSharpJson.deserialize<OutputEventRecord list> outputAfterResourceJson

        Assert.Equal("default-session", archive.SessionId)
        Assert.Equal(2, archive.EventCount)
        Assert.Equal(2, events.Length)
        Assert.Equal(events.Length, resourceEvents.Length)
        Assert.Equal<int64 array>([| 1L; 2L |], resourceEvents |> List.map (fun eventRecord -> eventRecord.SequenceNo) |> List.toArray)
        Assert.Single(resourceEventsAfter) |> ignore
        Assert.Equal("archived-beta", resourceEventsAfter[0].Payload)
    }

[<Fact>]
let ``McpResultTools seal pending tool and resource expose status and recovery`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore =
            FailOnceArchiveStore(JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore)
            :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore
            )

        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()

        let _ = service.PublishSessionOutput("stdout", "pending-alpha", executionId = "exec-pending-tool")
        let! _ = service.ExecuteOperation(ResetSession, "", timeout = TimeSpan.FromSeconds 30.0)

        let pendingJson =
            McpResultTools.GetSessionOutputSealPending(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let resultResource = ResultResources(service)
        let pendingResourceJson = resultResource.SessionOutputSealPending("default-host", "default-session")

        let recoveredJson =
            McpResultTools.RecoverSessionOutputSealPending(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let eventsJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                0L,
                0
            )

        let pending = FSharpJson.deserialize<SessionOutputSealPendingRecord option> pendingJson
        let pendingResource = FSharpJson.deserialize<SessionOutputSealPendingRecord option> pendingResourceJson
        let recovered = FSharpJson.deserialize<SessionOutputArchiveRecord option> recoveredJson
        let events = FSharpJson.deserialize<OutputEventRecord list> eventsJson

        Assert.True(pending.IsSome)
        Assert.True(pendingResource.IsSome)
        Assert.Contains("tool seal failure", pending.Value.ErrorMessage)
        Assert.True(recovered.IsSome)
        Assert.Equal(1, recovered.Value.EventCount)
        Assert.Single(events) |> ignore
        Assert.Equal("pending-alpha", events[0].Payload)
    }

[<Fact>]
let ``McpResultTools explicit seal session output archives live events without lifecycle reset`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore
            )

        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()

        let _ = service.PublishSessionOutput("stdout", "seal-alpha", executionId = "exec-seal-tool")
        let _ = service.PublishSessionOutput("stderr", "seal-beta", executionId = "exec-seal-tool")

        let sealedJson =
            McpResultTools.SealSessionOutput(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let eventsJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                0L,
                0
            )

        let sealOutcome = FSharpJson.deserialize<SessionOutputSealOutcome> sealedJson
        let events = FSharpJson.deserialize<OutputEventRecord list> eventsJson
        let archive = service.TryGetSessionOutputArchive()

        match sealOutcome with
        | Archived archived ->
            Assert.Equal("default-session", archived.SessionId)
            Assert.Equal(2, archived.EventCount)
        | SealPending pending ->
            failwithf "expected archived outcome but got pending: %s" pending.ErrorMessage

        Assert.True(archive.IsSome)
        Assert.Equal(2, archive.Value.EventCount)
        Assert.Equal(2, events.Length)
        Assert.Equal("seal-alpha", events[0].Payload)
        Assert.Equal("seal-beta", events[1].Payload)
    }

[<Fact>]
let ``McpResultTools archive tool and resource expose archive metadata after explicit seal`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore
            )

        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()
        let _ = service.PublishSessionOutput("stdout", "archive-alpha", executionId = "exec-archive-tool")

        let _ =
            McpResultTools.SealSessionOutput(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let archiveJson =
            McpResultTools.GetSessionOutputArchive(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let resourceArchiveJson =
            let resources = ResultResources(service)
            resources.SessionOutputArchive("default-host", "default-session")

        let archiveListJson = McpResultTools.ListSessionOutputArchives(service, 0)

        let resourceArchiveListJson =
            let resources = ResultResources(service)
            resources.SessionOutputArchives()

        let archivedEventsJson =
            let resources = ResultResources(service)
            resources.ArchivedSessionOutput("default-session")

        let archive = FSharpJson.deserialize<SessionOutputArchiveRecord option> archiveJson
        let resourceArchive = FSharpJson.deserialize<SessionOutputArchiveRecord option> resourceArchiveJson
        let archiveList = FSharpJson.deserialize<SessionOutputArchiveRecord list> archiveListJson
        let resourceArchiveList = FSharpJson.deserialize<SessionOutputArchiveRecord list> resourceArchiveListJson
        let archivedEvents = FSharpJson.deserialize<OutputEventRecord list> archivedEventsJson

        Assert.True(archive.IsSome)
        Assert.True(resourceArchive.IsSome)
        Assert.Equal("default-session", archive.Value.SessionId)
        Assert.Equal(1, archive.Value.EventCount)
        Assert.Equal(archive.Value.EventCount, resourceArchive.Value.EventCount)
        Assert.Single(archiveList) |> ignore
        Assert.Single(resourceArchiveList) |> ignore
        Assert.Equal("default-session", archiveList[0].SessionId)
        Assert.Equal("archive-alpha", archivedEvents[0].Payload)
    }

[<Fact>]
let ``McpResultTools list and session resources survive result registry reload`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore

        let service1 =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = (JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore),
                sessionOutputArchiveStore = (JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore),
                resultRegistry = (JsonLineResultRegistry(tempRoot) :> IResultRegistry)
            )

        use _cleanup1 = service1 :> IDisposable

        let! _ = service1.ExecuteOperation(ExecuteCode, "let persistedToolValue = 99", timeout = TimeSpan.FromSeconds 30.0)
        let! evalRecord = service1.ExecuteOperation(EvaluateExpression, "persistedToolValue", timeout = TimeSpan.FromSeconds 30.0)

        let service2 =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = (JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore),
                sessionOutputArchiveStore = (JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore),
                resultRegistry = (JsonLineResultRegistry(tempRoot) :> IResultRegistry)
            )

        use _cleanup2 = service2 :> IDisposable
        let _ = service2.ResolveRoute()

        let listJson = McpResultTools.ListFsiResults(service2, "default-agent", "", "")
        let singleJson = McpResultTools.GetFsiResult(service2, "default-agent", evalRecord.ResultId)
        let resources = ResultResources(service2)
        let sessionResultsJson = resources.SessionResults("default-host", "default-session")

        let listed = FSharpJson.deserialize<FsiExecutionRecord list> listJson
        let single = FSharpJson.deserialize<FsiExecutionRecord option> singleJson
        let sessionResults = FSharpJson.deserialize<FsiExecutionRecord list> sessionResultsJson

        Assert.True(single.IsSome)
        Assert.Equal(Some "99", single.Value.Result.Value)
        Assert.True(listed |> List.exists (fun record -> record.ResultId = evalRecord.ResultId))
        Assert.True(sessionResults |> List.exists (fun record -> record.ResultId = evalRecord.ResultId))
    }

[<Fact>]
let ``ResultResources resolve host-session route from registry instead of assuming default agent`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9400
                      FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9400/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9400"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9400
                          FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9400/user/fsi/supervisor"
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9400"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None })
            )

        let fsiClient =
            FakeFsiSupervisorClient(fun (_, sessionId) ->
                { SessionId = sessionId
                  Status = "ready"
                  Refs = []
                  Loads = []
                  SearchPaths = []
                  Variables = []
                  LastCheckpointId = None
                  RunningSinceUtc = Some DateTime.UtcNow })

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore,
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = (fsiClient :> IFsiSupervisorClient)
            )
        use _cleanup = service :> IDisposable

        let _ = McpControlPlaneTools.RegisterFsiAgent(service, "agent-output", "Agent Output")

        let! _ =
            McpControlPlaneTools.CreateFsiHost(
                service,
                "agent-output",
                "net10",
                "dotnet",
                "--dll\nfsi-host.dll",
                "/srv/fsi",
                "host-output",
                "",
                0
            )

        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-output", "host-output", "session-output", "Session Output")

        let route =
            { AgentId = "agent-output"
              HostId = "host-output"
              SessionId = "session-output" }

        let _ = service.PublishSessionOutput("stdout", "projected-output", requestedRoute = route, executionId = "exec-output")

        let resources = ResultResources(service)
        let eventsJson = resources.SessionOutput("host-output", "session-output")
        let pendingJson = resources.SessionOutputSealPending("host-output", "session-output")

        let events = FSharpJson.deserialize<OutputEventRecord list> eventsJson
        let pending = FSharpJson.deserialize<SessionOutputSealPendingRecord option> pendingJson

        Assert.Single(events) |> ignore
        Assert.Equal("projected-output", events[0].Payload)
        Assert.True(pending.IsNone)
    }
