module FsiMcpServiceTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open Akka.FSI.Contracts
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.Integration

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

let private waitForCompletion (service: FsiMcpService) asyncId =
    task {
        let mutable attempt = 0
        let mutable status = service.GetAsyncExecutionStatus(asyncId)

        while not status.IsCompleted && attempt < 100 do
            do! Task.Delay(100)
            attempt <- attempt + 1
            status <- service.GetAsyncExecutionStatus(asyncId)

        return status
    }

let private waitUntil (timeoutMs: int) (predicate: unit -> bool) =
    task {
        let started = DateTime.UtcNow
        let mutable done' = predicate ()

        while not done' && (DateTime.UtcNow - started).TotalMilliseconds < float timeoutMs do
            do! Task.Delay(50)
            done' <- predicate ()

        return done'
    }

let private createIsolatedService () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.FsiMcpServiceTests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore

    new FsiMcpService(
        NullLogger<FsiMcpService>.Instance,
        enableRemoteClient = false,
        sessionOutputLiveStore = (JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore),
        sessionOutputArchiveStore = (JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore),
        executionStore = (JsonLineResultRegistry(tempRoot) :> IExecutionStore)
    )

[<Fact>]
let ``FsiMcpService executes through default routed in-proc path and stores results`` () =
    task {
        let service = createIsolatedService ()
        use _cleanup = service :> IDisposable

        let! _ = service.ExecuteOperation(ExecuteCode, "let serviceValue = 7", timeout = TimeSpan.FromSeconds 30.0)
        let! evalRecord = service.ExecuteOperation(EvaluateExpression, "serviceValue", timeout = TimeSpan.FromSeconds 30.0)

        let route = service.ResolveRoute()
        let results = service.ListSessionResults(route)

        Assert.True(evalRecord.Result.IsSuccess)
        Assert.Equal(Some "7", evalRecord.Result.Value)
        Assert.True(results.Length >= 2)
        Assert.True(results |> List.exists (fun record -> record.ResultId = evalRecord.ResultId))
    }

[<Fact>]
let ``FsiMcpService execute operation auto publishes stdout to session output`` () =
    task {
        let service = createIsolatedService ()
        use _cleanup = service :> IDisposable

        let _ = service.SubscribeSessionOutput("ui-reader")
        let! record =
            service.ExecuteOperation(
                ExecuteCode,
                "printfn \"auto-published-output\"",
                timeout = TimeSpan.FromSeconds 30.0
            )

        let events = service.ListSessionOutput()
        let outputEvent =
            events
            |> List.find (fun eventRecord ->
                eventRecord.ExecutionId = Some record.ResultId
                && eventRecord.StreamKind = "stdout"
                && eventRecord.Payload.Contains("auto-published-output"))

        Assert.True(record.Result.IsSuccess)
        Assert.Contains("auto-published-output", record.Result.Output)
        Assert.Equal("default-session", outputEvent.SessionId)
        Assert.Equal(Some record.ResultId, outputEvent.ExecutionId)
    }

[<Fact>]
let ``FsiMcpService failed execute operation auto publishes stderr to session output`` () =
    task {
        let service = createIsolatedService ()
        use _cleanup = service :> IDisposable

        let! record =
            service.ExecuteOperation(
                ExecuteCode,
                "let broken =",
                timeout = TimeSpan.FromSeconds 30.0
            )

        let events = service.ListSessionOutput()
        let errorEvent =
            events
            |> List.find (fun eventRecord ->
                eventRecord.ExecutionId = Some record.ResultId
                && eventRecord.StreamKind = "stderr"
                && not (String.IsNullOrWhiteSpace eventRecord.Payload))

        Assert.False(record.Result.IsSuccess)
        Assert.Equal("default-session", errorEvent.SessionId)
        Assert.Equal(Some record.ResultId, errorEvent.ExecutionId)
    }

[<Fact>]
let ``FsiMcpService net10 remote execution publishes stdout to session output`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.FsiMcpServiceTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore

        let hostSpec =
            { ExecutablePath = "dotnet"
              Arguments = [ "fsi-host.dll" ]
              WorkingDirectory = Some "/srv/fsi"
              Role = Some "procnode"
              ProbeMessage = Some "PING"
              ProbeCron = None
              ProbeIntervalMs = Some 1000 }

        let procSnapshot procId spec =
            { ProcId = procId
              Status = "running"
              ProcessId = Some 9911
              FsiSupervisorPath = Some "akka://remote-output/user/fsi/supervisor"
              NodeAddress = Some "akka://remote-output"
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

        let fsiClient =
            { new IFsiSupervisorClient with
                member _.Execute(_host: HostRecord, request: FsiSupervisorExecRequest) =
                    Task.FromResult(
                        { SessionId = request.SessionId
                          RawErrorType = None
                          Result =
                            { Output = "remote stdout line"
                              Errors = ""
                              IsSuccess = true
                              ExecutionTime = Some(TimeSpan.FromMilliseconds 7.0)
                              Diagnostics = [||]
                              Value = Some "remote-value" } }
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
                    ) }

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = fsiClient,
                sessionOutputLiveStore = (JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore),
                sessionOutputArchiveStore = (JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore),
                executionStore = (JsonLineResultRegistry(tempRoot) :> IExecutionStore)
            )

        use _cleanup = service :> IDisposable

        let _ = service.RegisterAgent("agent-remote-output", "Agent Remote Output")
        let! _ = service.CreateHost("agent-remote-output", Net10Host, hostSpec, requestedHostId = "host-remote-output")
        let! _ = service.CreateSession("agent-remote-output", "host-remote-output", sessionId = "session-remote-output")

        let route =
            { AgentId = "agent-remote-output"
              HostId = "host-remote-output"
              SessionId = "session-remote-output" }

        let _ = service.SubscribeSessionOutput("remote-reader", requestedRoute = route)
        let! record = service.ExecuteOperation(ExecuteCode, "printfn \"remote stdout line\"", timeout = TimeSpan.FromSeconds 30.0, requestedRoute = route)
        let events = service.ListSessionOutput(requestedRoute = route)
        let outputEvent = events |> List.find (fun eventRecord -> eventRecord.ExecutionId = Some record.ResultId)

        Assert.Equal(Net10Remote, record.BackendKind)
        Assert.Equal("host-remote-output", record.HostId)
        Assert.Equal("session-remote-output", record.SessionId)
        Assert.Contains("remote stdout line", record.Result.Output)
        Assert.Equal("session-remote-output", outputEvent.SessionId)
        Assert.Equal("stdout", outputEvent.StreamKind)
        Assert.Equal("remote stdout line", outputEvent.Payload)
        Assert.Equal(Some record.ResultId, outputEvent.ExecutionId)
    }

[<Fact>]
let ``FsiMcpService async queue completes and exposes status`` () =
    task {
        let service = createIsolatedService ()
        use _cleanup = service :> IDisposable

        let asyncId = service.EnqueueExecuteCode("let asyncValue = 21", TimeSpan.FromSeconds 30.0)
        let! status = waitForCompletion service asyncId
        let! evalRecord = service.ExecuteOperation(EvaluateExpression, "asyncValue", timeout = TimeSpan.FromSeconds 30.0)

        Assert.True(status.Exists)
        Assert.True(status.IsCompleted)
        Assert.True(status.ResultId.IsSome)
        Assert.Equal(Some "default-agent", status.AgentId)
        Assert.Equal(Some "default-host", status.HostId)
        Assert.Equal(Some "default-session", status.SessionId)
        Assert.True(status.Result.IsSome)
        Assert.True(evalRecord.Result.IsSuccess)
        Assert.Equal(Some "21", evalRecord.Result.Value)
    }

[<Fact>]
let ``FsiMcpService async queue auto publishes stdout to session output`` () =
    task {
        let service = createIsolatedService ()
        use _cleanup = service :> IDisposable

        let asyncId =
            service.EnqueueExecuteCode(
                "printfn \"async-auto-output\"",
                TimeSpan.FromSeconds 30.0
            )

        let! status = waitForCompletion service asyncId
        let events = service.ListSessionOutput()

        Assert.True(status.Exists)
        Assert.True(status.IsCompleted)
        Assert.True(status.ResultId.IsSome)
        Assert.True(
            events
            |> List.exists (fun eventRecord ->
                eventRecord.ExecutionId = status.ResultId
                && eventRecord.StreamKind = "stdout"
                && eventRecord.Payload.Contains("async-auto-output"))
        )
    }

type private FailOnceArchiveStore(inner: ISessionOutputArchiveStore) =
    let mutable shouldFail = true

    interface ISessionOutputArchiveStore with
        member _.Seal(sessionId: string, events: OutputEventRecord list, archivedAt: DateTime) =
            if shouldFail then
                shouldFail <- false
                raise (InvalidOperationException("seal failed once"))
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

        member _.PruneArchives(?keepLatest: int, ?olderThanUtc: DateTime, ?dryRun: bool) =
            inner.PruneArchives(?keepLatest = keepLatest, ?olderThanUtc = olderThanUtc, ?dryRun = dryRun)

[<Fact>]
let ``FsiMcpService output subscriber broker tracks subscribers on default route`` () =
    let service = createIsolatedService ()
    use _cleanup = service :> IDisposable

    let subscription = service.SubscribeSessionOutput("ui-reader", fromSequenceNo = 3L, includeHistory = true)
    let subscribers = service.ListSessionOutputSubscribers()

    Assert.Equal("default-session", subscription.SessionId)
    Assert.Equal("ui-reader", subscription.SubscriberId)
    Assert.Equal(3L, subscription.FromSequenceNo)
    Assert.True(subscription.IncludeHistory)
    Assert.Single(subscribers) |> ignore

[<Fact>]
let ``FsiMcpService output subscriber broker publishes monotonic sequence and supports unsubscribe`` () =
    let service = createIsolatedService ()
    use _cleanup = service :> IDisposable

    let _ = service.SubscribeSessionOutput("ui-reader")
    let firstEvent, firstSubscribers = service.PublishSessionOutput("stdout", "hello", executionId = "exec-1")
    let secondEvent, secondSubscribers = service.PublishSessionOutput("stdout", "world", executionId = "exec-1")
    let removed = service.UnsubscribeSessionOutput("ui-reader")
    let thirdEvent, thirdSubscribers = service.PublishSessionOutput("stdout", "bye", executionId = "exec-1")

    Assert.Equal(1L, firstEvent.SequenceNo)
    Assert.Equal(2L, secondEvent.SequenceNo)
    Assert.Equal(3L, thirdEvent.SequenceNo)
    Assert.Single(firstSubscribers) |> ignore
    Assert.Single(secondSubscribers) |> ignore
    Assert.True(removed)
    Assert.Empty(thirdSubscribers)

[<Fact>]
let ``FsiMcpService contract subscription surface returns replay and unsubscribe diagnostics`` () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.FsiMcpServiceTests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore
    let broker = InMemoryOutputSubscriberBroker() :> IOutputSubscriberBroker
    let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
    let outputStore = SessionOutputStore(broker, liveStore) :> IOutputStore

    let service =
        new FsiMcpService(
            NullLogger<FsiMcpService>.Instance,
            enableRemoteClient = false,
            outputStore = outputStore,
            sessionOutputLiveStore = liveStore)

    use _cleanup = service :> IDisposable

    let firstEvent, _ = service.PublishSessionOutput("stdout", "alpha", executionId = "exec-contract")
    let secondEvent, _ = service.PublishSessionOutput("stderr", "beta", executionId = "exec-contract")

    let applied =
        service.SubscribeSessionOutputContract(
            { session = ""
              subscriberId = "codex"
              fromSequenceNo = Some 0L
              includeHistory = Some true }
        )

    let removed =
        service.UnsubscribeSessionOutputContract(
            { session = ""
              subscriberId = "codex" }
        )

    let missing =
        service.UnsubscribeSessionOutputContract(
            { session = ""
              subscriberId = "codex" }
        )

    Assert.True(applied.Subscription.accepted)
    Assert.Equal("default-session", applied.Subscription.session)
    Assert.Equal(Some 3L, applied.Subscription.nextSequenceNo)
    Assert.Equal(2, applied.ReplayEvents.Length)
    Assert.Equal(firstEvent.Payload, applied.ReplayEvents.[0].payload)
    Assert.Equal(secondEvent.Payload, applied.ReplayEvents.[1].payload)
    Assert.True(removed.accepted)
    Assert.False(missing.accepted)
    Assert.Equal(Some "subscriber was not registered", missing.message)

[<Fact>]
let ``FsiMcpService unified session output read returns archived events through same API`` () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.FsiMcpServiceTests", Guid.NewGuid().ToString("N"))
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

    let _ = service.PublishSessionOutput("stdout", "alpha", executionId = "exec-archive-1")
    let _ = service.PublishSessionOutput("stderr", "beta", executionId = "exec-archive-1")
    let archive =
        match service.SealSessionOutputArchive() with
        | Archived value -> value
        | SealPending pending -> failwithf "expected archived outcome but got pending: %s" pending.ErrorMessage
    let events = service.ListSessionOutput()
    let eventsAfter = service.ListSessionOutput(afterSequenceNo = 1L)

    Assert.Equal("default-session", archive.SessionId)
    Assert.Equal(2, archive.EventCount)
    Assert.Equal(Some 2L, archive.MaxSequenceNo)
    Assert.Equal(2, events.Length)
    Assert.Equal<int64 array>([| 1L; 2L |], events |> List.map (fun eventRecord -> eventRecord.SequenceNo) |> List.toArray)
    Assert.Single(eventsAfter) |> ignore
    Assert.Equal("beta", eventsAfter[0].Payload)

[<Fact>]
let ``FsiMcpService unregister session seals output and removes live registry`` () =
    let service = createIsolatedService ()
    use _cleanup = service :> IDisposable

    let route = service.ResolveRoute()
    let _ = service.PublishSessionOutput("stdout", "before-unregister", executionId = "exec-unregister-1", requestedRoute = route)
    let _ = service.PublishSessionOutput("stderr", "before-unregister-err", executionId = "exec-unregister-1", requestedRoute = route)

    let result = service.UnregisterSession(route.AgentId, route.HostId, route.SessionId)
    let archive = service.TryGetArchivedSessionOutputArchive(route.SessionId)
    let archivedEvents = service.ListArchivedSessionOutput(route.SessionId)

    Assert.True(result.IsSome)
    Assert.True(service.TryGetSession(route.HostId, route.SessionId).IsNone)

    match result.Value.ArchiveOutcome with
    | Archived archived ->
        Assert.Equal(route.SessionId, archived.SessionId)
        Assert.Equal(2, archived.EventCount)
    | SealPending pending -> failwithf "expected archived outcome but got pending: %s" pending.ErrorMessage

    Assert.True(archive.IsSome)
    Assert.Equal(2, archive.Value.EventCount)
    Assert.Equal(2, archivedEvents.Length)
    Assert.Equal("before-unregister", archivedEvents[0].Payload)
    Assert.Equal("before-unregister-err", archivedEvents[1].Payload)

[<Fact>]
let ``FsiMcpService reset seals session output into archive before lifecycle reset`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.FsiMcpServiceTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore,
                executionStore = (JsonLineResultRegistry(tempRoot) :> IExecutionStore)
            )

        use _cleanup = service :> IDisposable

        let _ = service.PublishSessionOutput("stdout", "before-reset", executionId = "exec-reset-1")
        let _ = service.PublishSessionOutput("stderr", "before-reset-err", executionId = "exec-reset-1")
        let! _ = service.ExecuteOperation(ResetSession, "", timeout = TimeSpan.FromSeconds 30.0)

        let archive = service.TryGetSessionOutputArchive()
        let events = service.ListSessionOutput()

        Assert.True(archive.IsSome)
        Assert.Equal(2, archive.Value.EventCount)
        Assert.Equal(2, events.Length)
        Assert.Equal("before-reset", events[0].Payload)
        Assert.Equal("before-reset-err", events[1].Payload)
    }

[<Fact>]
let ``FsiMcpService seal clears live cache and preserves monotonic sequence for subsequent output`` () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.FsiMcpServiceTests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore
    let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
    let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore
    let broker = InMemoryOutputSubscriberBroker() :> IOutputSubscriberBroker

    let service =
        new FsiMcpService(
            NullLogger<FsiMcpService>.Instance,
            enableRemoteClient = false,
            outputSubscriberBroker = broker,
            sessionOutputLiveStore = liveStore,
            sessionOutputArchiveStore = archiveStore
        )

    use _cleanup = service :> IDisposable

    let _ = service.PublishSessionOutput("stdout", "alpha", executionId = "exec-seal-1")
    let _ = service.PublishSessionOutput("stderr", "beta", executionId = "exec-seal-1")
    let archive =
        match service.SealSessionOutputArchive() with
        | Archived value -> value
        | SealPending pending -> failwithf "expected archived outcome but got pending: %s" pending.ErrorMessage
    let thirdEvent, _ = service.PublishSessionOutput("stdout", "gamma", executionId = "exec-seal-2")
    let events = service.ListSessionOutput()

    Assert.Equal(2, archive.EventCount)
    Assert.Equal(3L, thirdEvent.SequenceNo)
    Assert.Equal(3, events.Length)
    Assert.Equal<int64 array>([| 1L; 2L; 3L |], events |> List.map (fun eventRecord -> eventRecord.SequenceNo) |> List.toArray)
    Assert.Equal("gamma", events[2].Payload)

[<Fact>]
let ``FsiMcpService restart host seals current session output before lifecycle restart`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.FsiMcpServiceTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore,
                executionStore = (JsonLineResultRegistry(tempRoot) :> IExecutionStore)
            )

        use _cleanup = service :> IDisposable

        let _ = service.PublishSessionOutput("stdout", "before-restart", executionId = "exec-restart-1")
        let! record = service.ExecuteOperation(RestartHost, "", timeout = TimeSpan.FromSeconds 30.0)

        let archive = service.TryGetSessionOutputArchive()
        let events = service.ListSessionOutput()

        Assert.True(record.Result.IsSuccess)
        Assert.True(archive.IsSome)
        Assert.Equal(1, archive.Value.EventCount)
        Assert.Single(events) |> ignore
        Assert.Equal("before-restart", events[0].Payload)
    }

[<Fact>]
let ``FsiMcpService reset marks seal pending and allows recovery when archive seal fails`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.FsiMcpServiceTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let baseArchiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore
        let flakyArchiveStore = FailOnceArchiveStore(baseArchiveStore) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = flakyArchiveStore,
                executionStore = (JsonLineResultRegistry(tempRoot) :> IExecutionStore)
            )

        use _cleanup = service :> IDisposable

        let _ = service.PublishSessionOutput("stdout", "before-pending", executionId = "exec-pending-1")
        let! record = service.ExecuteOperation(ResetSession, "", timeout = TimeSpan.FromSeconds 30.0)

        let pending = service.TryGetSessionOutputSealPending()
        let eventsWhilePending = service.ListSessionOutput()
        let recovered = service.RecoverSessionOutputSealPending()
        let archive = service.TryGetSessionOutputArchive()
        let eventsAfterRecovery = service.ListSessionOutput()

        Assert.True(record.Result.IsSuccess)
        Assert.True(pending.IsSome)
        Assert.Contains("seal failed once", pending.Value.ErrorMessage)
        Assert.Single(eventsWhilePending) |> ignore
        Assert.Equal("before-pending", eventsWhilePending[0].Payload)
        Assert.True(recovered.IsSome)
        Assert.True(archive.IsSome)
        Assert.True(service.TryGetSessionOutputSealPending().IsNone)
        Assert.Single(eventsAfterRecovery) |> ignore
        Assert.Equal("before-pending", eventsAfterRecovery[0].Payload)
    }

[<Fact>]
let ``FsiMcpService can read persisted live output after service recreation`` () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.FsiMcpServiceTests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore
    let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
    let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

    let service1 =
        new FsiMcpService(
            NullLogger<FsiMcpService>.Instance,
            enableRemoteClient = false,
            sessionOutputLiveStore = liveStore,
            sessionOutputArchiveStore = archiveStore
        )

    use _cleanup1 = service1 :> IDisposable

    let _ = service1.PublishSessionOutput("stdout", "persisted-alpha", executionId = "exec-live-1")
    let _ = service1.PublishSessionOutput("stderr", "persisted-beta", executionId = "exec-live-1")

    let service2 =
        new FsiMcpService(
            NullLogger<FsiMcpService>.Instance,
            enableRemoteClient = false,
            sessionOutputLiveStore = (JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore),
            sessionOutputArchiveStore = (JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore)
        )

    use _cleanup2 = service2 :> IDisposable

    let events = service2.ListSessionOutput()

    Assert.Equal(2, events.Length)
    Assert.Equal<int64 array>([| 1L; 2L |], events |> List.map (fun eventRecord -> eventRecord.SequenceNo) |> List.toArray)
    Assert.Equal("persisted-alpha", events[0].Payload)
    Assert.Equal("persisted-beta", events[1].Payload)

[<Fact>]
let ``FsiMcpService can read persisted execution results after service recreation`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.FsiMcpServiceTests", Guid.NewGuid().ToString("N"))
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

        let! first = service1.ExecuteOperation(ExecuteCode, "let persistedResultValue = 42", timeout = TimeSpan.FromSeconds 30.0)
        let! second = service1.ExecuteOperation(EvaluateExpression, "persistedResultValue", timeout = TimeSpan.FromSeconds 30.0)
        let route = service1.ResolveRoute()

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

        let reloaded = service2.TryGetResult(second.ResultId)
        let sessionResults = service2.ListSessionResults(route)
        let agentResults = service2.ListAgentResults("default-agent")

        Assert.True(reloaded.IsSome)
        Assert.Equal(second.ResultId, reloaded.Value.ResultId)
        Assert.Equal(Some "42", reloaded.Value.Result.Value)
        Assert.True(sessionResults |> List.exists (fun record -> record.ResultId = first.ResultId))
        Assert.True(sessionResults |> List.exists (fun record -> record.ResultId = second.ResultId))
        Assert.True(agentResults |> List.exists (fun record -> record.ResultId = first.ResultId))
        Assert.True(agentResults |> List.exists (fun record -> record.ResultId = second.ResultId))
        Assert.True(sessionResults[0].SubmittedAt >= sessionResults[1].SubmittedAt)
    }

[<Fact>]
let ``FsiMcpService session liveness uses success cache within ttl`` () =
    task {
        let mutable getSessionInfoCalls = 0
        let hostSpec =
            { ExecutablePath = "dotnet"
              Arguments = [ "fsi-host.dll" ]
              WorkingDirectory = Some "/srv/fsi"
              Role = Some "procnode"
              ProbeMessage = Some "PING"
              ProbeCron = None
              ProbeIntervalMs = Some 1000 }

        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9901
                      FsiSupervisorPath = Some "akka://fsi-cache"
                      NodeAddress = Some "akka://node-cache"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9901
                          FsiSupervisorPath = Some "akka://fsi-cache"
                          NodeAddress = Some "akka://node-cache"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None })
            )

        let fsiClient =
            { new IFsiSupervisorClient with
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

                member _.GetSessionInfo(_host: HostRecord, sessionId: string) =
                    getSessionInfoCalls <- getSessionInfoCalls + 1

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
                    ) }

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = fsiClient,
                sessionLivenessSuccessTtl = TimeSpan.FromMinutes 5.0,
                sessionLivenessFailureBaseBackoff = TimeSpan.FromMinutes 5.0,
                sessionLivenessFailureMaxBackoff = TimeSpan.FromMinutes 5.0
            )

        use _cleanup = service :> IDisposable

        let _ = service.RegisterAgent("agent-live-cache", "Agent Live Cache")
        let! _ = service.CreateHost("agent-live-cache", Net10Host, hostSpec, requestedHostId = "host-live-cache")
        let! _ = service.CreateSession("agent-live-cache", "host-live-cache", "session-up", "Session Up")
        let callsBeforeLiveness = getSessionInfoCalls

        let! first = service.TryGetSessionLivenessForHostSession("host-live-cache", "session-up")
        let! second = service.TryGetSessionLivenessForHostSession("host-live-cache", "session-up")

        Assert.True(first.IsSome)
        Assert.True(second.IsSome)
        Assert.True(first.Value.IsReachable)
        Assert.True(second.Value.IsReachable)
        Assert.Equal(1, getSessionInfoCalls - callsBeforeLiveness)
    }

[<Fact>]
let ``FsiMcpService session liveness backs off repeated unreachable probes`` () =
    task {
        let mutable getSessionInfoCalls = 0
        let hostSpec =
            { ExecutablePath = "dotnet"
              Arguments = [ "fsi-host.dll" ]
              WorkingDirectory = Some "/srv/fsi"
              Role = Some "procnode"
              ProbeMessage = Some "PING"
              ProbeCron = None
              ProbeIntervalMs = Some 1000 }

        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9902
                      FsiSupervisorPath = Some "akka://fsi-backoff"
                      NodeAddress = Some "akka://node-backoff"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9902
                          FsiSupervisorPath = Some "akka://fsi-backoff"
                          NodeAddress = Some "akka://node-backoff"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None })
            )

        let fsiClient =
            { new IFsiSupervisorClient with
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

                member _.GetSessionInfo(_host: HostRecord, _sessionId: string) =
                    getSessionInfoCalls <- getSessionInfoCalls + 1
                    Task.FromException<FsiSupervisorSessionSnapshot>(InvalidOperationException("probe timeout"))

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
                    ) }

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = fsiClient,
                sessionLivenessSuccessTtl = TimeSpan.FromMinutes 5.0,
                sessionLivenessFailureBaseBackoff = TimeSpan.FromMinutes 5.0,
                sessionLivenessFailureMaxBackoff = TimeSpan.FromMinutes 5.0
            )

        use _cleanup = service :> IDisposable

        let _ = service.RegisterAgent("agent-live-backoff", "Agent Live Backoff")
        let! _ = service.CreateHost("agent-live-backoff", Net10Host, hostSpec, requestedHostId = "host-live-backoff")
        let! _ = service.CreateSession("agent-live-backoff", "host-live-backoff", "session-down", "Session Down")
        let callsBeforeLiveness = getSessionInfoCalls

        let! first = service.TryGetSessionLivenessForHostSession("host-live-backoff", "session-down")
        let! second = service.TryGetSessionLivenessForHostSession("host-live-backoff", "session-down")

        Assert.True(first.IsSome)
        Assert.True(second.IsSome)
        Assert.False(first.Value.IsReachable)
        Assert.False(second.Value.IsReachable)
        Assert.Equal("Unreachable", first.Value.Status)
        Assert.Equal("Unreachable", second.Value.Status)
        Assert.Contains("probe timeout", first.Value.ErrorMessage.Value)
        Assert.Equal(1, getSessionInfoCalls - callsBeforeLiveness)
    }

[<Fact>]
let ``FsiMcpService cached reachable liveness can become stale without extra probe`` () =
    task {
        let mutable getSessionInfoCalls = 0
        let hostSpec =
            { ExecutablePath = "dotnet"
              Arguments = [ "fsi-host.dll" ]
              WorkingDirectory = Some "/srv/fsi"
              Role = Some "procnode"
              ProbeMessage = Some "PING"
              ProbeCron = None
              ProbeIntervalMs = Some 1000 }

        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9903
                      FsiSupervisorPath = Some "akka://fsi-stale"
                      NodeAddress = Some "akka://node-stale"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9903
                          FsiSupervisorPath = Some "akka://fsi-stale"
                          NodeAddress = Some "akka://node-stale"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None })
            )

        let fsiClient =
            { new IFsiSupervisorClient with
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

                member _.GetSessionInfo(_host: HostRecord, sessionId: string) =
                    getSessionInfoCalls <- getSessionInfoCalls + 1

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
                    ) }

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = fsiClient,
                sessionLivenessSuccessTtl = TimeSpan.FromMinutes 5.0,
                sessionLivenessFailureBaseBackoff = TimeSpan.FromMinutes 5.0,
                sessionLivenessFailureMaxBackoff = TimeSpan.FromMinutes 5.0,
                sessionLivenessStaleAfter = TimeSpan.Zero
            )

        use _cleanup = service :> IDisposable

        let _ = service.RegisterAgent("agent-live-stale", "Agent Live Stale")
        let! _ = service.CreateHost("agent-live-stale", Net10Host, hostSpec, requestedHostId = "host-live-stale")
        let! _ = service.CreateSession("agent-live-stale", "host-live-stale", "session-up", "Session Up")
        let callsBeforeLiveness = getSessionInfoCalls

        let! first = service.TryGetSessionLivenessForHostSession("host-live-stale", "session-up")
        let! second = service.TryGetSessionLivenessForHostSession("host-live-stale", "session-up")

        Assert.True(first.IsSome)
        Assert.True(second.IsSome)
        Assert.False(first.Value.IsStale)
        Assert.True(second.Value.IsStale)
        Assert.True(second.Value.IsReachable)
        Assert.Equal(1, getSessionInfoCalls - callsBeforeLiveness)
    }

[<Fact>]
let ``FsiMcpService background liveness sweep eventually probes registered host sessions`` () =
    task {
        let mutable probeCount = 0

        let hostSpec =
            { ExecutablePath = "dotnet"
              Arguments = [ "--dll"; "fsi-host.dll" ]
              WorkingDirectory = Some "/srv/fsi"
              Role = None
              ProbeMessage = Some "PING"
              ProbeCron = None
              ProbeIntervalMs = Some 1000 }

        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9152
                      FsiSupervisorPath = Some "akka.tcp://proc-system@127.0.0.1:9152/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://proc-system@127.0.0.1:9152"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun _ ->
                    Some
                        { ProcId = "proc-bg-sweep"
                          Status = "running"
                          ProcessId = Some 9152
                          FsiSupervisorPath = Some "akka.tcp://proc-system@127.0.0.1:9152/user/fsi/supervisor"
                          NodeAddress = Some "akka.tcp://proc-system@127.0.0.1:9152"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None })
            )

        let fsiClient =
            { new IFsiSupervisorClient with
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

                member _.GetSessionInfo(_host: HostRecord, sessionId: string) =
                    probeCount <- probeCount + 1

                    Task.FromResult(
                        { SessionId = sessionId
                          Status = "ready"
                          Refs = []
                          Loads = []
                          SearchPaths = []
                          Variables = []
                          LastCheckpointId = Some "cp-bg"
                          RunningSinceUtc = Some DateTime.UtcNow }
                    )

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
                    ) }

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = fsiClient,
                sessionLivenessSuccessTtl = TimeSpan.FromMinutes 5.0,
                sessionLivenessBackgroundSweepInterval = TimeSpan.FromMilliseconds 100.0
            )

        use _cleanup = service :> IDisposable

        let _ = service.RegisterAgent("agent-bg-sweep", "Agent Background Sweep")
        let! _ = service.CreateHost("agent-bg-sweep", Net10Host, hostSpec, requestedHostId = "host-bg-sweep")
        let! _ = service.CreateSession("agent-bg-sweep", "host-bg-sweep", "session-bg-sweep", "Session Background Sweep")
        let callsBeforeWait = probeCount

        let! observed = waitUntil 5000 (fun () -> probeCount > callsBeforeWait)

        Assert.True(observed)
        Assert.True(probeCount > callsBeforeWait)
    }
