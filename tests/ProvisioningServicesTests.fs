module ProvisioningServicesTests

open System
open System.Threading.Tasks
open Akka.Actor
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.Backends
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.Integration

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

type private FakeSessionProvisioningBackend(initialStateFactory: ExecutionRoute -> SessionRecord, executeFactory: ExecutionRequest -> FsiExecutionRecord) =
    let mutable executeRequests : ExecutionRequest list = []

    member _.ExecuteRequests = List.rev executeRequests

    interface IFsiExecutionBackend with
        member _.BackendKind = Net10Remote

        member _.Execute(request: ExecutionRequest) =
            executeRequests <- request :: executeRequests
            Task.FromResult(executeFactory request)

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

        let provisioning = HostProvisioningService(agentRegistry, hostRegistry, procClient :> IProcSupervisorClient)

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

        let provisioning = HostProvisioningService(agentRegistry, hostRegistry, procClient :> IProcSupervisorClient)

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

        let provisioning = HostProvisioningService(agentRegistry, hostRegistry, procClient :> IProcSupervisorClient)

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

        let provisioning = HostProvisioningService(agentRegistry, hostRegistry, procClient :> IProcSupervisorClient)

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

        let provisioning = HostProvisioningService(agentRegistry, hostRegistry, procClient :> IProcSupervisorClient)

        let! host = provisioning.CreateHost("agent-list-fallback", Net10Host, spec, requestedHostId = "host-list-fallback")

        Assert.Equal(Ready, host.Status)
        Assert.Equal(Some 9041, host.ProcId)
        Assert.Equal(Some "akka.tcp://FsiExecutionSystem@localhost:9041/user/fsi/supervisor", host.Address)
    }

[<Fact>]
let ``SessionProvisioningService bootstraps missing session through backend execute`` () =
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

        let mutable firstLookup = true

        let backend =
            FakeSessionProvisioningBackend(
                (fun route ->
                    if firstLookup then
                        firstLookup <- false

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
                          Status = SessionReady
                          Refs = []
                          Loads = []
                          SearchPaths = []
                          Variables = []
                          LastCheckpointId = None
                          RunningSinceUtc = Some DateTime.UtcNow
                          LastExecutionAt = Some DateTime.UtcNow }),
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
                          Value = None } })
            )
            :> IFsiExecutionBackend

        let selector = BackendSelector([ backend ])
        let provisioning = SessionProvisioningService(hostRegistry, sessionRegistry, selector)

        let! session =
            provisioning.CreateSession("agent-net10", "host-net10", sessionId = "session-c", sessionName = "Session C")

        let stored = sessionRegistry.TryGet("host-net10", "session-c") |> Option.get
        let fakeBackend = backend :?> FakeSessionProvisioningBackend

        Assert.Equal(SessionReady, session.Status)
        Assert.Equal("Session C", session.SessionName)
        Assert.Equal("session-c", stored.SessionId)
        Assert.Contains(fakeBackend.ExecuteRequests, fun request -> request.OperationKind = ExecuteCode && request.Payload = "()")
    }
