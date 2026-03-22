module McpControlPlaneToolsTests

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.Backends
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

[<Fact>]
let ``McpControlPlaneTools register host session and health flow works`` () =
    task {
        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
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

        let agent = JsonSerializer.Deserialize<AgentRecord>(agentJson)
        let host = JsonSerializer.Deserialize<HostRecord>(hostJson)
        let session = JsonSerializer.Deserialize<SessionRecord>(sessionJson)
        let hosts = JsonSerializer.Deserialize<HostRecord list>(hostsJson)
        let sessions = JsonSerializer.Deserialize<SessionRecord list>(sessionsJson)
        let health : BackendHealth = JsonSerializer.Deserialize<BackendHealth>(healthJson)

        Assert.Equal("agent-cp", agent.AgentId)
        Assert.Equal("host-cp", host.HostId)
        Assert.Equal(Net10Host, host.HostKind)
        Assert.Equal("session-cp", session.SessionId)
        Assert.Equal("Session Control", session.SessionName)
        Assert.Contains(hosts, fun value -> value.HostId = "host-cp")
        Assert.Contains(sessions, fun value -> value.SessionId = "session-cp")
        Assert.True(health.IsAvailable)
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
        let mappingsJson = resources.PathMappings()

        Assert.Contains("agent-r", agentJson)
        Assert.Contains("host-r", hostJson)
        Assert.Contains("session-r", hostSessionsJson)
        Assert.Contains("session-r", sessionJson)
        Assert.Equal("[]", mappingsJson)
    }
