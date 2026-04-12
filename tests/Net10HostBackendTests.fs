module Net10HostBackendTests

open System
open System.Threading.Tasks
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.Backends
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.Integration

type private FakeProcSupervisorClient
    (
        infoFactory: string -> Task<ProcHostSnapshot option>,
        ?listFactory: unit -> Task<ProcHostSnapshot list>,
        ?restartFactory: string -> ProcHostSnapshot
    ) =
    let mutable restarted : string list = []
    let listFactory = defaultArg listFactory (fun () -> Task.FromResult([]))

    let restartFactory =
        defaultArg
            restartFactory
            (fun procId ->
                { ProcId = procId
                  Status = "running"
                  ProcessId = Some 8080
                  FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:8110/user/fsi/supervisor"
                  NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:8110"
                  LastProbeUtc = Some DateTime.UtcNow
                  LastProbeOk = Some true
                  ProbeFailures = 0
                  Spec = None
                  LastError = None })

    member _.Restarted = List.rev restarted

    interface IProcSupervisorClient with
        member _.StartProc(_, _) = Task.FromException<ProcHostSnapshot>(InvalidOperationException("StartProc is not used in this test."))
        member _.StopProc(_, _) = Task.FromException<ProcHostSnapshot>(InvalidOperationException("StopProc is not used in this test."))
        member _.GetProcInfo(procId: string) = infoFactory procId
        member _.ListProcInfo() = listFactory ()

        member _.RestartProc(procId: string) =
            restarted <- procId :: restarted
            Task.FromResult(restartFactory procId)

type private FakeFsiSupervisorClient
    (
        execFactory: HostRecord * FsiSupervisorExecRequest -> FsiSupervisorExecutionResult,
        sessionFactory: HostRecord * string -> FsiSupervisorSessionSnapshot,
        ensureFactory: HostRecord * string -> FsiSupervisorEnsureResult,
        resetFactory: HostRecord * string -> FsiSupervisorResetResult,
        ?listFactory: HostRecord -> FsiSupervisorSessionSnapshot list
    ) =
    let mutable executeRequests : (HostRecord * FsiSupervisorExecRequest) list = []
    let mutable sessionRequests : (HostRecord * string) list = []
    let mutable ensureRequests : (HostRecord * string) list = []
    let mutable resetRequests : (HostRecord * string) list = []
    let listFactory = defaultArg listFactory (fun _ -> [])

    member _.ExecuteRequests = List.rev executeRequests
    member _.SessionRequests = List.rev sessionRequests
    member _.EnsureRequests = List.rev ensureRequests
    member _.ResetRequests = List.rev resetRequests

    interface IFsiSupervisorClient with
        member _.Execute(host: HostRecord, request: FsiSupervisorExecRequest) =
            executeRequests <- (host, request) :: executeRequests
            Task.FromResult(execFactory (host, request))

        member _.GetSessionInfo(host: HostRecord, sessionId: string) =
            sessionRequests <- (host, sessionId) :: sessionRequests
            Task.FromResult(sessionFactory (host, sessionId))

        member _.ListSessions(host: HostRecord) = Task.FromResult(listFactory host)

        member _.EnsureSession(host: HostRecord, sessionId: string) =
            ensureRequests <- (host, sessionId) :: ensureRequests
            Task.FromResult(ensureFactory (host, sessionId))

        member _.ResetSession(host: HostRecord, sessionId: string) =
            resetRequests <- (host, sessionId) :: resetRequests
            Task.FromResult(resetFactory (host, sessionId))

let private createHostRegistry () =
    let registry = InMemoryHostRegistry() :> IHostRegistry
    let host =
        { HostId = "net10-host-1"
          AgentId = "agent-net10"
          HostKind = Net10Host
          BackendKind = Net10Remote
          Status = Ready
          Address = Some "akka.tcp://FsiExecutionSystem@localhost:8110/user/fsi/supervisor"
          ProcId = Some 8080
          CreatedAt = DateTime.UtcNow
          LastHealthCheckAt = Some DateTime.UtcNow
          LastError = None }

    registry.Create(host) |> ignore
    registry, host

[<Fact>]
let ``Net10HostBackend maps nuget and path operations into supervisor execution requests`` () =
    task {
        let hostRegistry, host = createHostRegistry ()

        let fakeFsiClient =
            FakeFsiSupervisorClient(
                (fun (_, request) ->
                    { SessionId = request.SessionId
                      RawErrorType = None
                      Result =
                        { Output = request.Code
                          Errors = ""
                          IsSuccess = true
                          ExecutionTime = Some(TimeSpan.FromMilliseconds 5.0)
                          Diagnostics = [||]
                          Value = None } }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Status = "ready"
                      Refs = []
                      Loads = []
                      SearchPaths = []
                      Variables = []
                      LastCheckpointId = None
                      RunningSinceUtc = Some DateTime.UtcNow }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = false
                      Status = "created" }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = true
                      Status = "reset" })
            )

        let fakeProcClient =
            FakeProcSupervisorClient(fun _ ->
                Task.FromResult(
                    Some
                        { ProcId = host.HostId
                          Status = "running"
                          ProcessId = host.ProcId
                          FsiSupervisorPath = host.Address
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:8110"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None }))

        let backend =
            Net10HostBackend(hostRegistry, fakeFsiClient :> IFsiSupervisorClient, fakeProcClient :> IProcSupervisorClient)
            :> IFsiExecutionBackend

        let route =
            { AgentId = "agent-net10"
              HostId = host.HostId
              SessionId = "session-a" }

        let! _ =
            backend.Execute(
                { RequestId = "req-nuget"
                  Route = route
                  OperationKind = ReferenceNuget
                  Payload = "Newtonsoft.Json, 13.0.3"
                  Timeout = Some(TimeSpan.FromSeconds 30.0)
                  UsePackageTargets = None }
            )

        let! _ =
            backend.Execute(
                { RequestId = "req-path"
                  Route = route
                  OperationKind = AddSearchPath
                  Payload = "/workspace/libs"
                  Timeout = Some(TimeSpan.FromSeconds 30.0)
                  UsePackageTargets = None }
            )

        let requests = fakeFsiClient.ExecuteRequests
        let nugetRequest = requests |> List.find (fun (_, request) -> request.RequestId = "req-nuget") |> snd
        let pathRequest = requests |> List.find (fun (_, request) -> request.RequestId = "req-path") |> snd

        Assert.Equal("#r \"nuget: Newtonsoft.Json, 13.0.3\"", nugetRequest.Code)
        Assert.Equal("#I \"/workspace/libs\"", pathRequest.Code)
        Assert.Equal("session-a", nugetRequest.SessionId)
    }

[<Fact>]
let ``Net10HostBackend maps session snapshot into SessionRecord`` () =
    task {
        let hostRegistry, host = createHostRegistry ()

        let fakeFsiClient =
            FakeFsiSupervisorClient(
                (fun (_, request) ->
                    { SessionId = request.SessionId
                      RawErrorType = None
                      Result = FsiResult.empty }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Status = "busy"
                      Refs = [ "a.dll" ]
                      Loads = [ "boot.fsx" ]
                      SearchPaths = [ "/tmp" ]
                      Variables = [ "value", "int" ]
                      LastCheckpointId = Some "cp-1"
                      RunningSinceUtc = Some DateTime.UtcNow }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = true
                      Status = "ready" }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = true
                      Status = "reset" })
            )

        let fakeProcClient =
            FakeProcSupervisorClient(fun _ ->
                Task.FromResult(
                    Some
                        { ProcId = host.HostId
                          Status = "running"
                          ProcessId = host.ProcId
                          FsiSupervisorPath = host.Address
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:8110"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None }))

        let backend =
            Net10HostBackend(hostRegistry, fakeFsiClient :> IFsiSupervisorClient, fakeProcClient :> IProcSupervisorClient)
            :> IFsiExecutionBackend

        let route =
            { AgentId = "agent-net10"
              HostId = host.HostId
              SessionId = "session-b" }

        let! state = backend.GetSessionState(route)

        Assert.Equal(SessionBusy, state.Status)
        Assert.Contains("a.dll", state.Refs)
        Assert.Contains("/tmp", state.SearchPaths)
        Assert.Equal(Some "cp-1", state.LastCheckpointId)
    }

[<Fact>]
let ``Net10HostBackend health check and restart delegate to ProcSupervisor`` () =
    task {
        let hostRegistry, host = createHostRegistry ()

        let fakeFsiClient =
            FakeFsiSupervisorClient(
                (fun (_, request) ->
                    { SessionId = request.SessionId
                      RawErrorType = None
                      Result = FsiResult.empty }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Status = "ready"
                      Refs = []
                      Loads = []
                      SearchPaths = []
                      Variables = []
                      LastCheckpointId = None
                      RunningSinceUtc = Some DateTime.UtcNow }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = true
                      Status = "ready" }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = true
                      Status = "reset" })
            )

        let fakeProcClient =
            FakeProcSupervisorClient(fun _ ->
                Task.FromResult(
                    Some
                        { ProcId = host.HostId
                          Status = "running"
                          ProcessId = host.ProcId
                          FsiSupervisorPath = host.Address
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:8110"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None }))

        let backend =
            Net10HostBackend(hostRegistry, fakeFsiClient :> IFsiSupervisorClient, fakeProcClient :> IProcSupervisorClient)
            :> IFsiExecutionBackend

        do! backend.RestartHost(host)
        let! health = backend.HealthCheck(host)

        Assert.Contains(host.HostId, fakeProcClient.Restarted)
        Assert.True(health.IsAvailable)
        Assert.Equal(Some host.HostId, health.HostId)
    }

[<Fact>]
let ``Net10HostBackend health check falls back to ListProcInfo when direct lookup times out`` () =
    task {
        let hostRegistry, host = createHostRegistry ()

        let fakeFsiClient =
            FakeFsiSupervisorClient(
                (fun (_, request) ->
                    { SessionId = request.SessionId
                      RawErrorType = None
                      Result = FsiResult.empty }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Status = "ready"
                      Refs = []
                      Loads = []
                      SearchPaths = []
                      Variables = []
                      LastCheckpointId = None
                      RunningSinceUtc = Some DateTime.UtcNow }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = true
                      Status = "ready" }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = true
                      Status = "reset" })
            )

        let fakeProcClient =
            FakeProcSupervisorClient(
                (fun _ -> Task.FromException<ProcHostSnapshot option>(Akka.Actor.AskTimeoutException("Timeout after 5.00 seconds"))),
                (fun () ->
                    Task.FromResult(
                        [ { ProcId = host.HostId
                            Status = "running"
                            ProcessId = host.ProcId
                            FsiSupervisorPath = host.Address
                            NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:8110"
                            LastProbeUtc = Some DateTime.UtcNow
                            LastProbeOk = Some true
                            ProbeFailures = 0
                            Spec = None
                            LastError = Some "healthy via list" } ])))

        let backend =
            Net10HostBackend(hostRegistry, fakeFsiClient :> IFsiSupervisorClient, fakeProcClient :> IProcSupervisorClient)
            :> IFsiExecutionBackend

        let! health = backend.HealthCheck(host)

        Assert.True(health.IsAvailable)
        Assert.Equal(Some "healthy via list", health.Message)
    }

[<Fact>]
let ``Net10HostBackend reset session delegates to supervisor and returns success record`` () =
    task {
        let hostRegistry, host = createHostRegistry ()

        let fakeFsiClient =
            FakeFsiSupervisorClient(
                (fun (_, request) ->
                    { SessionId = request.SessionId
                      RawErrorType = None
                      Result = FsiResult.empty }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Status = "ready"
                      Refs = []
                      Loads = []
                      SearchPaths = []
                      Variables = []
                      LastCheckpointId = None
                      RunningSinceUtc = Some DateTime.UtcNow }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = true
                      Status = "ready" }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = true
                      Status = "reset" })
            )

        let fakeProcClient =
            FakeProcSupervisorClient(fun _ ->
                Task.FromResult(
                    Some
                        { ProcId = host.HostId
                          Status = "running"
                          ProcessId = host.ProcId
                          FsiSupervisorPath = host.Address
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:8110"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None }))

        let backend =
            Net10HostBackend(hostRegistry, fakeFsiClient :> IFsiSupervisorClient, fakeProcClient :> IProcSupervisorClient)
            :> IFsiExecutionBackend

        let route =
            { AgentId = "agent-net10"
              HostId = host.HostId
              SessionId = "session-reset" }

        let! result = backend.ResetSession(route)

        Assert.True(result.Result.IsSuccess)
        Assert.Equal("FSI session reset", result.Result.Output)
        Assert.Equal(Some "reset", result.Result.Value)
        Assert.Equal(route.SessionId, result.SessionId)
        let resetRequests = fakeFsiClient.ResetRequests |> List.toArray
        Assert.Single(resetRequests) |> ignore
        Assert.Equal(host.HostId, fst resetRequests.[0] |> fun value -> value.HostId)
        Assert.Equal(route.SessionId, snd resetRequests.[0])
    }

[<Fact>]
let ``Net10HostBackend ensure session delegates to supervisor and returns hydrated session state`` () =
    task {
        let hostRegistry, host = createHostRegistry ()

        let fakeFsiClient =
            FakeFsiSupervisorClient(
                (fun (_, request) ->
                    { SessionId = request.SessionId
                      RawErrorType = None
                      Result = FsiResult.empty }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Status = "ready"
                      Refs = [ "A.dll" ]
                      Loads = [ "init.fsx" ]
                      SearchPaths = [ "c:/fsi" ]
                      Variables = [ "x", "42" ]
                      LastCheckpointId = Some "cp-1"
                      RunningSinceUtc = Some DateTime.UtcNow }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = false
                      Status = "created" }),
                (fun (_, sessionId) ->
                    { SessionId = sessionId
                      Existed = true
                      Status = "reset" })
            )

        let fakeProcClient =
            FakeProcSupervisorClient(fun _ ->
                Task.FromResult(
                    Some
                        { ProcId = host.HostId
                          Status = "running"
                          ProcessId = host.ProcId
                          FsiSupervisorPath = host.Address
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:8110"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None }))

        let backend =
            Net10HostBackend(hostRegistry, fakeFsiClient :> IFsiSupervisorClient, fakeProcClient :> IProcSupervisorClient)
            :> IFsiExecutionBackend

        let route =
            { AgentId = "agent-net10"
              HostId = host.HostId
              SessionId = "session-ensure" }

        let! state = backend.EnsureSession(route)

        Assert.Equal(SessionReady, state.Status)
        Assert.Contains("A.dll", state.Refs)
        Assert.Contains("init.fsx", state.Loads)
        Assert.Contains("c:/fsi", state.SearchPaths)
        Assert.Equal(Some "42", state.Variables |> List.tryFind (fun (name, _) -> name = "x") |> Option.map snd)
        Assert.Single(fakeFsiClient.EnsureRequests |> List.toArray) |> ignore
        Assert.Single(fakeFsiClient.SessionRequests |> List.toArray) |> ignore
    }
