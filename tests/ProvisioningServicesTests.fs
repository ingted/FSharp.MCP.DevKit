module ProvisioningServicesTests

open System
open System.Threading.Tasks
open Akka.Actor
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.Backends
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.Integration

let private createInventoryStore () = InMemoryInventoryEventStore() :> IInventoryEventStore

type private FakeProcSupervisorClient
    (
        startFactory: string * ProcHostSpec -> Task<ProcHostSnapshot>,
        ?getFactory: string -> Task<ProcHostSnapshot option>,
        ?listFactory: unit -> Task<ProcHostSnapshot list>
    ) =
    let mutable starts : (string * ProcHostSpec) list = []
    let getFactory = defaultArg getFactory (fun _ -> Task.FromResult(None))
    let listFactory = defaultArg listFactory (fun () -> Task.FromResult([]))

    member _.Starts = List.rev starts

    interface IProcSupervisorClient with
        member _.StartProc(procId: string, spec: ProcHostSpec) =
            starts <- (procId, spec) :: starts
            startFactory (procId, spec)

        member _.StopProc(_, _) = Task.FromException<ProcHostSnapshot>(InvalidOperationException("StopProc is not used in this test."))
        member _.GetProcInfo(procId) = getFactory procId
        member _.ListProcInfo() = listFactory ()
        member _.RestartProc(_) = Task.FromException<ProcHostSnapshot>(InvalidOperationException("RestartProc is not used in this test."))

type private FakeSessionProvisioningBackend
    (
        initialStateFactory: ExecutionRoute -> SessionRecord,
        executeFactory: ExecutionRequest -> FsiExecutionRecord,
        ?ensureStateFactory: ExecutionRoute -> SessionRecord
    ) =
    let mutable executeRequests : ExecutionRequest list = []
    let mutable ensureRequests : ExecutionRoute list = []
    let ensureStateFactory = defaultArg ensureStateFactory initialStateFactory

    member _.ExecuteRequests = List.rev executeRequests
    member _.EnsureRequests = List.rev ensureRequests

    interface IFsiExecutionBackend with
        member _.BackendKind = Net10Remote

        member _.Execute(request: ExecutionRequest) =
            executeRequests <- request :: executeRequests
            Task.FromResult(executeFactory request)

        member _.EnsureSession(route: ExecutionRoute) =
            ensureRequests <- route :: ensureRequests
            Task.FromResult(ensureStateFactory route)

        member _.GetSessionState(route: ExecutionRoute) = Task.FromResult(initialStateFactory route)
        member _.ResetSession(route: ExecutionRoute) = Task.FromResult(executeFactory { RequestId = Guid.NewGuid().ToString("N"); Route = route; OperationKind = ResetSession; Payload = ""; Timeout = None; UsePackageTargets = None })
        member _.RestartHost(_) = task { return () }
        member _.HealthCheck(host: HostRecord) = Task.FromResult({ BackendKind = Net10Remote; IsAvailable = true; Message = Some "ok"; HostId = Some host.HostId; CheckedAt = DateTime.UtcNow })

[<Fact>]
let ``HostProvisioningService rejects explicit inproc host creation`` () =
    task {
        let agentRegistry = InMemoryAgentRegistry() :> IAgentRegistry
        let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
        let procClient =
            FakeProcSupervisorClient(fun _ ->
                Task.FromException<ProcHostSnapshot>(InvalidOperationException("StartProc should not be called for InProcHost")))

        let provisioning = HostProvisioningService(agentRegistry, hostRegistry, procClient :> IProcSupervisorClient, createInventoryStore ())

        let spec =
            { ExecutablePath = "dotnet"
              Arguments = [ "run" ]
              WorkingDirectory = Some "/tmp"
              Role = None
              ProbeMessage = Some "ping"
              ProbeCron = None
              ProbeIntervalMs = Some 1000 }

        let! ex =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                provisioning.CreateHost("agent-1", InProcHost, spec) :> Task)

        Assert.Contains("InProcHost", ex.Message)
    }

[<Fact>]
let ``HostProvisioningService starts proc and stores ready net10 host`` () =
    task {
        let agentRegistry = InMemoryAgentRegistry() :> IAgentRegistry
        let hostRegistry = InMemoryHostRegistry() :> IHostRegistry

        let procClient =
            FakeProcSupervisorClient(fun (procId, spec) ->
                Task.FromResult(
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9020
                      FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9020/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9020"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }))

        let eventStore = createInventoryStore ()
        let provisioning = HostProvisioningService(agentRegistry, hostRegistry, procClient :> IProcSupervisorClient, eventStore)

        let spec =
            { ExecutablePath = "dotnet"
              Arguments = [ "fsi-host.dll"; "--port"; "9020" ]
              WorkingDirectory = Some "/srv/fsi"
              Role = None
              ProbeMessage = Some "PING"
              ProbeCron = None
              ProbeIntervalMs = Some 1000 }

        let! host = provisioning.CreateHost("agent-net10", Net10Host, spec, requestedHostId = "host-net10")
        let stored = hostRegistry.TryGet("host-net10") |> Option.get

        Assert.Equal(Net10Remote, host.BackendKind)
        Assert.Equal(Ready, host.Status)
        Assert.Equal(Some 9020, host.ProcId)
        Assert.Equal(Some "akka.tcp://FsiExecutionSystem@localhost:9020/user/fsi/supervisor", host.Address)
        Assert.Equal(host.HostId, stored.HostId)
        let events = eventStore.List()
        Assert.Single(events) |> ignore
        Assert.Equal("host.upserted", events.Head.EventKind)
    }

[<Fact>]
let ``HostProvisioningService recovers from StartProc ask timeout by polling proc info`` () =
    task {
        let agentRegistry = InMemoryAgentRegistry() :> IAgentRegistry
        let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
        let mutable getCalls = 0

        let spec =
            { ExecutablePath = "dotnet"
              Arguments = [ "fsi-host.dll"; "--port"; "9021" ]
              WorkingDirectory = Some "/srv/fsi"
              Role = None
              ProbeMessage = None
              ProbeCron = None
              ProbeIntervalMs = None }

        let procClient =
            FakeProcSupervisorClient(
                (fun _ -> Task.FromException<ProcHostSnapshot>(AskTimeoutException("Timeout after 5.00 seconds"))),
                (fun procId ->
                    getCalls <- getCalls + 1

                    if getCalls < 2 then
                        Task.FromResult(None)
                    else
                        Task.FromResult(
                            Some
                                { ProcId = procId
                                  Status = "running"
                                  ProcessId = Some 9021
                                  FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9021/user/fsi/supervisor"
                                  NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9021"
                                  LastProbeUtc = Some DateTime.UtcNow
                                  LastProbeOk = Some true
                                  ProbeFailures = 0
                                  Spec = Some spec
                                  LastError = None })))

        let provisioning = HostProvisioningService(agentRegistry, hostRegistry, procClient :> IProcSupervisorClient, createInventoryStore ())

        let! host = provisioning.CreateHost("agent-recover", Net10Host, spec, requestedHostId = "host-recover")

        Assert.Equal(Ready, host.Status)
        Assert.Equal(Some 9021, host.ProcId)
        Assert.True(getCalls >= 2)
    }

[<Fact>]
let ``HostProvisioningService waits for supervisor address after successful StartProc`` () =
    task {
        let agentRegistry = InMemoryAgentRegistry() :> IAgentRegistry
        let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
        let mutable getCalls = 0

        let initialSnapshot =
            { ProcId = "host-address"
              Status = "starting"
              ProcessId = Some 9031
              FsiSupervisorPath = None
              NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9031"
              LastProbeUtc = Some DateTime.UtcNow
              LastProbeOk = Some true
              ProbeFailures = 0
              Spec = None
              LastError = None }

        let finalizedSnapshot =
            { initialSnapshot with
                Status = "running"
                FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9031/user/fsi/supervisor" }

        let procClient =
            FakeProcSupervisorClient(
                (fun _ -> Task.FromResult initialSnapshot),
                (fun _ ->
                    getCalls <- getCalls + 1
                    if getCalls < 2 then
                        Task.FromResult(Some initialSnapshot)
                    else
                        Task.FromResult(Some finalizedSnapshot)))

        let provisioning = HostProvisioningService(agentRegistry, hostRegistry, procClient :> IProcSupervisorClient, createInventoryStore ())

        let spec =
            { ExecutablePath = "dotnet"
              Arguments = [ "fsi-host.dll"; "--port"; "9031" ]
              WorkingDirectory = Some "/srv/fsi"
              Role = None
              ProbeMessage = None
              ProbeCron = None
              ProbeIntervalMs = None }

        let! host = provisioning.CreateHost("agent-address", Net10Host, spec, requestedHostId = "host-address")

        Assert.Equal(Ready, host.Status)
        Assert.Equal(Some "akka.tcp://FsiExecutionSystem@localhost:9031/user/fsi/supervisor", host.Address)
        Assert.True(getCalls >= 2)
    }

[<Fact>]
let ``HostProvisioningService falls back to ListProcInfo when GetProcInfo ask times out`` () =
    task {
        let agentRegistry = InMemoryAgentRegistry() :> IAgentRegistry
        let hostRegistry = InMemoryHostRegistry() :> IHostRegistry

        let spec =
            { ExecutablePath = "dotnet"
              Arguments = [ "fsi-host.dll"; "--port"; "9041" ]
              WorkingDirectory = Some "/srv/fsi"
              Role = None
              ProbeMessage = None
              ProbeCron = None
              ProbeIntervalMs = None }

        let procClient =
            FakeProcSupervisorClient(
                (fun _ -> Task.FromException<ProcHostSnapshot>(AskTimeoutException("Timeout after 5.00 seconds"))),
                (fun _ -> Task.FromException<ProcHostSnapshot option>(AskTimeoutException("Timeout after 5.00 seconds"))),
                (fun () ->
                    Task.FromResult(
                        [ { ProcId = "host-list-fallback"
                            Status = "running"
                            ProcessId = Some 9041
                            FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9041/user/fsi/supervisor"
                            NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9041"
                            LastProbeUtc = Some DateTime.UtcNow
                            LastProbeOk = Some true
                            ProbeFailures = 0
                            Spec = Some spec
                            LastError = None } ])))

        let provisioning = HostProvisioningService(agentRegistry, hostRegistry, procClient :> IProcSupervisorClient, createInventoryStore ())

        let! host = provisioning.CreateHost("agent-list-fallback", Net10Host, spec, requestedHostId = "host-list-fallback")

        Assert.Equal(Ready, host.Status)
        Assert.Equal(Some 9041, host.ProcId)
        Assert.Equal(Some "akka.tcp://FsiExecutionSystem@localhost:9041/user/fsi/supervisor", host.Address)
    }

[<Fact>]
let ``SessionProvisioningService ensures missing session through backend without bootstrap execute`` () =
    task {
        let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
        let sessionRegistry = InMemorySessionRegistry() :> ISessionRegistry
        let now = DateTime.UtcNow

        hostRegistry.Create(
            { HostId = "host-net10"
              AgentId = "agent-net10"
              HostKind = Net10Host
              BackendKind = Net10Remote
              Status = Ready
              Address = Some "akka.tcp://FsiExecutionSystem@localhost:9020/user/fsi/supervisor"
              ProcId = Some 9020
              CreatedAt = now
              LastHealthCheckAt = Some now
              LastError = None }
        )
        |> ignore

        let backend =
            FakeSessionProvisioningBackend(
                (fun route ->
                    { SessionId = route.SessionId
                      AgentId = route.AgentId
                      HostId = route.HostId
                      SessionName = route.SessionId
                      Status = SessionMissing
                      Refs = []
                      Loads = []
                      SearchPaths = []
                      Variables = []
                      LastCheckpointId = None
                      RunningSinceUtc = None
                      LastExecutionAt = None }),
                (fun request ->
                    { ResultId = "result-bootstrap"
                      RequestId = request.RequestId
                      AgentId = request.Route.AgentId
                      BackendKind = Net10Remote
                      HostId = request.Route.HostId
                      SessionId = request.Route.SessionId
                      OperationKind = request.OperationKind
                      SubmittedAt = DateTime.UtcNow
                      StartedAt = Some DateTime.UtcNow
                      CompletedAt = Some DateTime.UtcNow
                      RawErrorType = None
                      Result =
                        { Output = ""
                          Errors = ""
                          IsSuccess = true
                          ExecutionTime = Some(TimeSpan.FromMilliseconds 5.0)
                          Diagnostics = [||]
                          Value = None } }),
                (fun route ->
                    { SessionId = route.SessionId
                      AgentId = route.AgentId
                      HostId = route.HostId
                      SessionName = route.SessionId
                      Status = SessionReady
                      Refs = []
                      Loads = []
                      SearchPaths = []
                      Variables = []
                      LastCheckpointId = None
                      RunningSinceUtc = Some DateTime.UtcNow
                      LastExecutionAt = Some DateTime.UtcNow })
            )
            :> IFsiExecutionBackend

        let selector = BackendSelector([ backend ])
        let eventStore = createInventoryStore ()
        let provisioning = SessionProvisioningService(hostRegistry, sessionRegistry, selector, eventStore)

        let! session =
            provisioning.CreateSession("agent-net10", "host-net10", sessionId = "session-c", sessionName = "Session C")

        let stored = sessionRegistry.TryGet("host-net10", "session-c") |> Option.get
        let fakeBackend = backend :?> FakeSessionProvisioningBackend

        Assert.Equal(SessionReady, session.Status)
        Assert.Equal("Session C", session.SessionName)
        Assert.Equal("session-c", stored.SessionId)
        Assert.Single(fakeBackend.EnsureRequests) |> ignore
        Assert.Empty(fakeBackend.ExecuteRequests)
        let events = eventStore.List()
        Assert.Single(events) |> ignore
        Assert.Equal("session.upserted", events.Head.EventKind)
    }

[<Fact>]
let ``SessionProvisioningService keeps backend-visible session state when it already exists`` () =
    task {
        let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
        let sessionRegistry = InMemorySessionRegistry() :> ISessionRegistry
        let now = DateTime.UtcNow

        hostRegistry.Create(
            { HostId = "host-delayed"
              AgentId = "agent-delayed"
              HostKind = Net10Host
              BackendKind = Net10Remote
              Status = Ready
              Address = Some "akka.tcp://FsiExecutionSystem@localhost:9030/user/fsi/supervisor"
              ProcId = Some 9030
              CreatedAt = now
              LastHealthCheckAt = Some now
              LastError = None }
        )
        |> ignore

        let backend =
            FakeSessionProvisioningBackend(
                (fun route ->
                    { SessionId = route.SessionId
                      AgentId = route.AgentId
                      HostId = route.HostId
                      SessionName = route.SessionId
                      Status = SessionReady
                      Refs = []
                      Loads = []
                      SearchPaths = []
                      Variables = [ "ready", "true" ]
                      LastCheckpointId = None
                      RunningSinceUtc = Some DateTime.UtcNow
                      LastExecutionAt = Some DateTime.UtcNow }),
                (fun request ->
                    { ResultId = "result-delayed"
                      RequestId = request.RequestId
                      AgentId = request.Route.AgentId
                      BackendKind = Net10Remote
                      HostId = request.Route.HostId
                      SessionId = request.Route.SessionId
                      OperationKind = request.OperationKind
                      SubmittedAt = DateTime.UtcNow
                      StartedAt = Some DateTime.UtcNow
                      CompletedAt = Some DateTime.UtcNow
                      RawErrorType = None
                      Result =
                        { Output = ""
                          Errors = ""
                          IsSuccess = true
                          ExecutionTime = Some(TimeSpan.FromMilliseconds 5.0)
                          Diagnostics = [||]
                          Value = None } })
            )
            :> IFsiExecutionBackend

        let selector = BackendSelector([ backend ])
        let provisioning = SessionProvisioningService(hostRegistry, sessionRegistry, selector, createInventoryStore ())

        let! session =
            provisioning.CreateSession("agent-delayed", "host-delayed", sessionId = "session-delayed")

        Assert.Equal(SessionReady, session.Status)
        Assert.Equal(Some("true"), session.Variables |> List.tryFind (fun (name, _) -> name = "ready") |> Option.map snd)
        Assert.True(sessionRegistry.TryGet("host-delayed", "session-delayed").IsSome)
    }

[<Fact>]
let ``SessionProvisioningService ignores bootstrap execute timeout because ensure path does not call Execute`` () =
    task {
        let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
        let sessionRegistry = InMemorySessionRegistry() :> ISessionRegistry
        let now = DateTime.UtcNow

        hostRegistry.Create(
            { HostId = "host-exec-timeout"
              AgentId = "agent-exec-timeout"
              HostKind = Net10Host
              BackendKind = Net10Remote
              Status = Ready
              Address = Some "akka.tcp://FsiExecutionSystem@localhost:9050/user/fsi/supervisor"
              ProcId = Some 9050
              CreatedAt = now
              LastHealthCheckAt = Some now
              LastError = None }
        )
        |> ignore

        let mutable lookupCount = 0
        let mutable executeCalls = 0

        let backend =
            { new IFsiExecutionBackend with
                member _.BackendKind = Net10Remote

                member _.Execute(_request: ExecutionRequest) =
                    executeCalls <- executeCalls + 1
                    Task.FromException<FsiExecutionRecord>(AskTimeoutException("Timeout after 30.00 seconds"))

                member _.EnsureSession(route: ExecutionRoute) =
                    lookupCount <- lookupCount + 1
                    Task.FromResult(
                        { SessionId = route.SessionId
                          AgentId = route.AgentId
                          HostId = route.HostId
                          SessionName = route.SessionId
                          Status = SessionReady
                          Refs = []
                          Loads = []
                          SearchPaths = []
                          Variables = []
                          LastCheckpointId = None
                          RunningSinceUtc = Some DateTime.UtcNow
                          LastExecutionAt = None })

                member _.GetSessionState(route: ExecutionRoute) =
                    lookupCount <- lookupCount + 1

                    let state =
                        if lookupCount < 2 then
                            { SessionId = route.SessionId
                              AgentId = route.AgentId
                              HostId = route.HostId
                              SessionName = route.SessionId
                              Status = SessionMissing
                              Refs = []
                              Loads = []
                              SearchPaths = []
                              Variables = []
                              LastCheckpointId = None
                              RunningSinceUtc = None
                              LastExecutionAt = None }
                        else
                            { SessionId = route.SessionId
                              AgentId = route.AgentId
                              HostId = route.HostId
                              SessionName = route.SessionId
                              Status = SessionMissing
                              Refs = []
                              Loads = []
                              SearchPaths = []
                              Variables = []
                              LastCheckpointId = None
                              RunningSinceUtc = None
                              LastExecutionAt = None }

                    Task.FromResult state

                member _.ResetSession(route: ExecutionRoute) =
                    Task.FromResult(
                        { ResultId = "result-reset"
                          RequestId = Guid.NewGuid().ToString("N")
                          AgentId = route.AgentId
                          BackendKind = Net10Remote
                          HostId = route.HostId
                          SessionId = route.SessionId
                          OperationKind = ResetSession
                          SubmittedAt = DateTime.UtcNow
                          StartedAt = Some DateTime.UtcNow
                          CompletedAt = Some DateTime.UtcNow
                          RawErrorType = None
                          Result =
                            { Output = ""
                              Errors = ""
                              IsSuccess = true
                              ExecutionTime = Some(TimeSpan.FromMilliseconds 5.0)
                              Diagnostics = [||]
                              Value = None } })

                member _.RestartHost(_) = task { return () }

                member _.HealthCheck(host: HostRecord) =
                    Task.FromResult(
                        { BackendKind = Net10Remote
                          IsAvailable = true
                          Message = Some "ok"
                          HostId = Some host.HostId
                          CheckedAt = DateTime.UtcNow }) }

        let selector = BackendSelector([ backend ])
        let provisioning = SessionProvisioningService(hostRegistry, sessionRegistry, selector, createInventoryStore ())

        let! session =
            provisioning.CreateSession("agent-exec-timeout", "host-exec-timeout", sessionId = "session-timeout")

        Assert.Equal(SessionReady, session.Status)
        Assert.True(lookupCount >= 1)
        Assert.Equal(0, executeCalls)
        Assert.True(sessionRegistry.TryGet("host-exec-timeout", "session-timeout").IsSome)
    }

[<Fact>]
let ``SessionProvisioningService registers ensured session when registry was empty`` () =
    task {
        let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
        let sessionRegistry = InMemorySessionRegistry() :> ISessionRegistry
        let now = DateTime.UtcNow

        hostRegistry.Create(
            { HostId = "host-missing"
              AgentId = "agent-missing"
              HostKind = Net10Host
              BackendKind = Net10Remote
              Status = Ready
              Address = Some "akka.tcp://FsiExecutionSystem@localhost:9040/user/fsi/supervisor"
              ProcId = Some 9040
              CreatedAt = now
              LastHealthCheckAt = Some now
              LastError = None }
        )
        |> ignore

        let backend =
            FakeSessionProvisioningBackend(
                (fun route ->
                    { SessionId = route.SessionId
                      AgentId = route.AgentId
                      HostId = route.HostId
                      SessionName = route.SessionId
                      Status = SessionMissing
                      Refs = []
                      Loads = []
                      SearchPaths = []
                      Variables = []
                      LastCheckpointId = None
                      RunningSinceUtc = None
                      LastExecutionAt = None }),
                (fun request ->
                    { ResultId = "result-missing"
                      RequestId = request.RequestId
                      AgentId = request.Route.AgentId
                      BackendKind = Net10Remote
                      HostId = request.Route.HostId
                      SessionId = request.Route.SessionId
                      OperationKind = request.OperationKind
                      SubmittedAt = DateTime.UtcNow
                      StartedAt = Some DateTime.UtcNow
                      CompletedAt = Some DateTime.UtcNow
                      RawErrorType = None
                      Result =
                        { Output = ""
                          Errors = ""
                          IsSuccess = true
                          ExecutionTime = Some(TimeSpan.FromMilliseconds 5.0)
                          Diagnostics = [||]
                          Value = None } }),
                (fun route ->
                    { SessionId = route.SessionId
                      AgentId = route.AgentId
                      HostId = route.HostId
                      SessionName = route.SessionId
                      Status = SessionReady
                      Refs = []
                      Loads = []
                      SearchPaths = []
                      Variables = []
                      LastCheckpointId = None
                      RunningSinceUtc = Some DateTime.UtcNow
                      LastExecutionAt = None })
            )
            :> IFsiExecutionBackend

        let selector = BackendSelector([ backend ])
        let provisioning = SessionProvisioningService(hostRegistry, sessionRegistry, selector, createInventoryStore ())

        let! session =
            provisioning.CreateSession("agent-missing", "host-missing", sessionId = "session-missing")

        Assert.Equal(SessionReady, session.Status)
        Assert.True(sessionRegistry.TryGet("host-missing", "session-missing").IsSome)
    }
