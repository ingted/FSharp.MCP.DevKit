module McpControlPlaneToolsTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.Backends
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.Integration
open FSharp.MCP.DevKit.Server.McpFsiTools

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
let ``McpControlPlaneTools register host session and health flow works`` () =
    task {
        let mutable capturedSpec : ProcHostSpec option = None

        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    capturedSpec <- Some spec
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9100
                      FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9100/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9100"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9100
                          FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9100/user/fsi/supervisor"
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9100"
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
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = (fsiClient :> IFsiSupervisorClient)
            )

        use _cleanup = service :> IDisposable

        let agentJson = McpControlPlaneTools.RegisterFsiAgent(service, "agent-cp", "Control Plane Agent")
        let! hostJson =
            McpControlPlaneTools.CreateFsiHost(
                service,
                "agent-cp",
                "net10",
                "dotnet",
                "--dll\nfsi-host.dll",
                "/srv/fsi",
                "host-cp",
                "PING",
                1000
            )

        let! sessionJson = McpControlPlaneTools.CreateFsiSession(service, "agent-cp", "host-cp", "session-cp", "Session Control")
        let hostsJson = McpControlPlaneTools.ListFsiHosts(service, "agent-cp")
        let sessionsJson = McpControlPlaneTools.ListFsiSessions(service, "host-cp")
        let! healthJson = McpControlPlaneTools.GetFsiHostHealth(service, "host-cp")

        let agent = FSharpJson.deserialize<AgentRecord> agentJson
        let host = FSharpJson.deserialize<HostRecord> hostJson
        let session = FSharpJson.deserialize<SessionRecord> sessionJson
        let hosts = FSharpJson.deserialize<HostRecord list> hostsJson
        let sessions = FSharpJson.deserialize<SessionRecord list> sessionsJson
        let health : BackendHealth = FSharpJson.deserialize<BackendHealth> healthJson

        Assert.Equal("agent-cp", agent.AgentId)
        Assert.Equal("host-cp", host.HostId)
        Assert.Equal(Net10Host, host.HostKind)
        Assert.Equal("session-cp", session.SessionId)
        Assert.Equal("Session Control", session.SessionName)
        Assert.Equal(None, capturedSpec |> Option.bind (fun spec -> spec.Role))
        Assert.Contains(hosts, fun value -> value.HostId = "host-cp")
        Assert.Contains(sessions, fun value -> value.SessionId = "session-cp")
        Assert.True(health.IsAvailable)
    }

[<Fact>]
let ``CreateFsiHost tokenizes shell-like argument string into argv`` () =
    task {
        let mutable capturedSpec : ProcHostSpec option = None

        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    capturedSpec <- Some spec
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9300
                      FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9300/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9300"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun _ -> None)
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
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = (fsiClient :> IFsiSupervisorClient)
            )

        use _cleanup = service :> IDisposable

        let! _ =
            McpControlPlaneTools.CreateFsiHost(
                service,
                "agent-tokenize",
                "net10",
                "dotnet",
                "exec --runtimeconfig /app/test.runtimeconfig.json --depsfile /app/test.deps.json \"/app/My Host.dll\" --flag \"two words\" --role procnode",
                "/app",
                "host-tokenize",
                "",
                0
            )

        let spec = capturedSpec |> Option.defaultWith (fun () -> failwith "expected captured proc spec")

        let expectedArgs : string list =
            [ "exec"
              "--runtimeconfig"
              "/app/test.runtimeconfig.json"
              "--depsfile"
              "/app/test.deps.json"
              "/app/My Host.dll"
              "--flag"
              "two words"
              "--role"
              "procnode" ]

        Assert.Equal<string>(expectedArgs, spec.Arguments)
    }

[<Fact>]
let ``ControlPlaneResources expose registered host and session`` () =
    task {
        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9200
                      FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9200/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9200"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9200
                          FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9200/user/fsi/supervisor"
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9200"
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
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = (fsiClient :> IFsiSupervisorClient)
            )

        use _cleanup = service :> IDisposable

        let _ = McpControlPlaneTools.RegisterFsiAgent(service, "agent-r", "Agent R")

        let! _ =
            McpControlPlaneTools.CreateFsiHost(
                service,
                "agent-r",
                "net10",
                "dotnet",
                "--dll\nfsi-host.dll",
                "/srv/fsi",
                "host-r",
                "PING",
                1000
            )

        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-r", "host-r", "session-r", "Session R")

        let resources = ControlPlaneResources(service)

        let agentJson = resources.Agent("agent-r")
        let hostJson = resources.Host("host-r")
        let hostSessionsJson = resources.HostSessions("host-r")
        let sessionJson = resources.HostSession("host-r", "session-r")
        let inventoryEventsJson = resources.InventoryEvents()
        let inventoryEventsAfterJson = resources.InventoryEventsAfter(0L)
        let mappingsJson = resources.PathMappings()

        Assert.Contains("agent-r", agentJson)
        Assert.Contains("host-r", hostJson)
        Assert.Contains("session-r", hostSessionsJson)
        Assert.Contains("session-r", sessionJson)
        Assert.Contains("host.upserted", inventoryEventsJson)
        Assert.Contains("session.upserted", inventoryEventsAfterJson)
        Assert.Equal("[]", mappingsJson)
    }

[<Fact>]
let ``ControlPlaneResources expose backend session state resource`` () =
    task {
        let runningSince = DateTime.UtcNow.AddMinutes(-5.0)

        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9250
                      FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9250/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9250"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9250
                          FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9250/user/fsi/supervisor"
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9250"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None })
            )

        let fsiClient =
            FakeFsiSupervisorClient(fun (_, sessionId) ->
                { SessionId = sessionId
                  Status = "faulted"
                  Refs = []
                  Loads = []
                  SearchPaths = []
                  Variables = [ "x", "1" ]
                  LastCheckpointId = Some "cp-1"
                  RunningSinceUtc = Some runningSince })

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = (fsiClient :> IFsiSupervisorClient)
            )

        use _cleanup = service :> IDisposable

        let _ = McpControlPlaneTools.RegisterFsiAgent(service, "agent-state", "Agent State")

        let! _ =
            McpControlPlaneTools.CreateFsiHost(
                service,
                "agent-state",
                "net10",
                "dotnet",
                "--dll\nfsi-host.dll",
                "/srv/fsi",
                "host-state",
                "PING",
                1000
            )

        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-state", "host-state", "session-state", "Session State")

        let resources = ControlPlaneResources(service)
        let! sessionStateJson = resources.HostSessionState("host-state", "session-state")
        let sessionState = FSharpJson.deserialize<SessionRecord option> sessionStateJson

        Assert.True(sessionState.IsSome)
        Assert.Equal("session-state", sessionState.Value.SessionId)
        Assert.Equal(SessionFaulted, sessionState.Value.Status)
        Assert.Equal(Some "cp-1", sessionState.Value.LastCheckpointId)
        Assert.Equal(Some runningSince, sessionState.Value.RunningSinceUtc)
    }

[<Fact>]
let ``ControlPlaneResources expose session liveness resource with reachable and unreachable states`` () =
    task {
        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9260
                      FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9260/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9260"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9260
                          FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9260/user/fsi/supervisor"
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9260"
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
                    if sessionId = "session-down" then
                        Task.FromException<FsiSupervisorSessionSnapshot>(InvalidOperationException("remote timeout"))
                    else
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
                fsiSupervisorClient = fsiClient
            )

        use _cleanup = service :> IDisposable

        let _ = McpControlPlaneTools.RegisterFsiAgent(service, "agent-live", "Agent Live")

        let! _ =
            McpControlPlaneTools.CreateFsiHost(
                service,
                "agent-live",
                "net10",
                "dotnet",
                "--dll\nfsi-host.dll",
                "/srv/fsi",
                "host-live",
                "PING",
                1000
            )

        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-live", "host-live", "session-up", "Session Up")
        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-live", "host-live", "session-down", "Session Down")

        let resources = ControlPlaneResources(service)
        let! reachableJson = resources.HostSessionLiveness("host-live", "session-up")
        let! unreachableJson = resources.HostSessionLiveness("host-live", "session-down")

        let reachable = FSharpJson.deserialize<SessionLivenessRecord option> reachableJson
        let unreachable = FSharpJson.deserialize<SessionLivenessRecord option> unreachableJson

        Assert.True(reachable.IsSome)
        Assert.True(reachable.Value.IsReachable)
        Assert.False(reachable.Value.IsStale)
        Assert.Equal("SessionReady", reachable.Value.Status)
        Assert.Equal(0, reachable.Value.ConsecutiveFailures)
        Assert.True(reachable.Value.NextProbeNotBeforeUtc.IsSome)

        Assert.True(unreachable.IsSome)
        Assert.False(unreachable.Value.IsReachable)
        Assert.Equal("Unreachable", unreachable.Value.Status)
        Assert.False(unreachable.Value.IsStale)
        Assert.Equal(1, unreachable.Value.ConsecutiveFailures)
        Assert.True(unreachable.Value.NextProbeNotBeforeUtc.IsSome)
        Assert.Contains("remote timeout", unreachable.Value.ErrorMessage.Value)
    }

[<Fact>]
let ``ControlPlaneResources expose host sessions liveness batch resource`` () =
    task {
        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9270
                      FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9270/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9270"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9270
                          FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9270/user/fsi/supervisor"
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9270"
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
                    if sessionId = "session-down" then
                        Task.FromException<FsiSupervisorSessionSnapshot>(InvalidOperationException("remote timeout"))
                    else
                        Task.FromResult(
                            { SessionId = sessionId
                              Status = "ready"
                              Refs = []
                              Loads = []
                              SearchPaths = []
                              Variables = []
                              LastCheckpointId = Some $"cp-{sessionId}"
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
                fsiSupervisorClient = fsiClient
            )

        use _cleanup = service :> IDisposable

        let _ = McpControlPlaneTools.RegisterFsiAgent(service, "agent-live-batch", "Agent Live Batch")

        let! _ =
            McpControlPlaneTools.CreateFsiHost(
                service,
                "agent-live-batch",
                "net10",
                "dotnet",
                "--dll\nfsi-host.dll",
                "/srv/fsi",
                "host-live-batch",
                "PING",
                1000
            )

        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-live-batch", "host-live-batch", "session-up", "Session Up")
        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-live-batch", "host-live-batch", "session-down", "Session Down")

        let resources = ControlPlaneResources(service)
        let! payloadJson = resources.HostSessionsLiveness("host-live-batch")
        let payload = FSharpJson.deserialize<SessionLivenessRecord list> payloadJson

        Assert.Equal(2, payload.Length)
        Assert.Contains(payload, fun item -> item.SessionId = "session-up" && item.IsReachable)
        Assert.Contains(payload, fun item -> item.SessionId = "session-down" && not item.IsReachable)
    }

[<Fact>]
let ``McpControlPlaneTools probe host sessions liveness bypasses existing cache once`` () =
    task {
        let mutable getSessionInfoCount = 0

        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9271
                      FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9271/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9271"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9271
                          FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9271/user/fsi/supervisor"
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9271"
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
                    getSessionInfoCount <- getSessionInfoCount + 1

                    Task.FromResult(
                        { SessionId = sessionId
                          Status = "ready"
                          Refs = []
                          Loads = []
                          SearchPaths = []
                          Variables = []
                          LastCheckpointId = Some "cp-probe"
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
                sessionLivenessSuccessTtl = TimeSpan.FromMinutes 5.0
            )

        use _cleanup = service :> IDisposable

        let _ = McpControlPlaneTools.RegisterFsiAgent(service, "agent-live-probe", "Agent Live Probe")

        let! _ =
            McpControlPlaneTools.CreateFsiHost(
                service,
                "agent-live-probe",
                "net10",
                "dotnet",
                "--dll\nfsi-host.dll",
                "/srv/fsi",
                "host-live-probe",
                "PING",
                1000
            )

        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-live-probe", "host-live-probe", "session-up", "Session Up")
        let! _ = service.TryGetSessionLivenessForHostSession("host-live-probe", "session-up")

        let beforeProbe = getSessionInfoCount
        let! payloadJson = McpControlPlaneTools.ProbeFsiHostSessionsLiveness(service, "host-live-probe")
        let payload = FSharpJson.deserialize<SessionLivenessRecord list> payloadJson

        Assert.True(beforeProbe >= 1)
        Assert.Equal(beforeProbe + 1, getSessionInfoCount)
        Assert.Single(payload) |> ignore
        Assert.True(payload.Head.IsReachable)
        Assert.Equal("session-up", payload.Head.SessionId)
    }

[<Fact>]
let ``EnsureFsiRoute materializes legacy default route without ProcSupervisor`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! ensuredJson =
            McpControlPlaneTools.EnsureFsiRoute(
                service,
                DefaultRouting.DefaultAgentId,
                "Default Agent",
                DefaultRouting.DefaultHostId,
                DefaultRouting.DefaultSessionId,
                ""
            )

        let ensured = FSharpJson.deserialize<EnsureRouteResponse> ensuredJson

        Assert.Equal(DefaultRouting.DefaultAgentId, ensured.Route.AgentId)
        Assert.Equal(DefaultRouting.DefaultHostId, ensured.Route.HostId)
        Assert.Equal(DefaultRouting.DefaultSessionId, ensured.Route.SessionId)
        Assert.Equal(InProcHost, ensured.Host.HostKind)
        Assert.True(ensured.CreatedAgent)
        Assert.False(ensured.CreatedHost)
        Assert.True(ensured.CreatedSession)
        Assert.Contains("Resolved to the legacy in-proc default route.", ensured.Notes)
    }

[<Fact>]
let ``FsiMcpService EnsureRoute creates missing host and session when spec is provided`` () =
    task {
        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9300
                      FsiSupervisorPath = Some "akka://fsi-demo"
                      NodeAddress = Some "akka://node-demo"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9300
                          FsiSupervisorPath = Some "akka://fsi-demo"
                          NodeAddress = Some "akka://node-demo"
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
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = (fsiClient :> IFsiSupervisorClient)
            )

        use _cleanup = service :> IDisposable

        let! ensured =
            service.EnsureRoute(
                "agent-ensure",
                displayName = "Ensure Agent",
                hostId = "host-ensure",
                sessionId = "session-ensure",
                sessionName = "Ensure Session",
                hostKind = Net10Host,
                hostSpec =
                    { ExecutablePath = "dotnet"
                      Arguments = [ "--dll"; "fsi-host.dll" ]
                      WorkingDirectory = Some "/srv/fsi"
                      Role = None
                      ProbeMessage = Some "PING"
                      ProbeCron = None
                      ProbeIntervalMs = Some 1000 }
            )

        Assert.Equal("agent-ensure", ensured.Route.AgentId)
        Assert.Equal("host-ensure", ensured.Route.HostId)
        Assert.Equal("session-ensure", ensured.Route.SessionId)
        Assert.Equal(Net10Host, ensured.Host.HostKind)
        Assert.Equal("Ensure Session", ensured.Session.SessionName)
        Assert.True(ensured.CreatedAgent)
        Assert.True(ensured.CreatedHost)
        Assert.True(ensured.CreatedSession)
        Assert.Contains("execute_f_sharp_code_routed", ensured.RecommendedNextTools)
    }
