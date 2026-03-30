namespace FSharp.MCP.DevKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Microsoft.Extensions.Logging.Abstractions
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.Integration
open FSharp.MCP.DevKit.Server.McpFsiTools
open Xunit

[<Collection("mcp-client-e2e")>]
type RealNet10HostIsolationTests() =

    static member private RepoRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

    static member private ServerOutputDir =
        Path.Combine(RealNet10HostIsolationTests.RepoRoot, "src", "FSharp.MCP.DevKit.Server", "bin", "Debug", "net10.0")

    static member private ServerRuntimeConfigPath =
        Path.Combine(RealNet10HostIsolationTests.ServerOutputDir, "FSharp.MCP.DevKit.runtimeconfig.json")

    static member private ServerDepsPath =
        Path.Combine(RealNet10HostIsolationTests.ServerOutputDir, "FSharp.MCP.DevKit.deps.json")

    static member private ProcSupervisorDllPath =
        Path.Combine(RealNet10HostIsolationTests.ServerOutputDir, "Akka.Proc.Supervisor.dll")

    static member private AkkaConfigPath =
        Path.Combine(RealNet10HostIsolationTests.RepoRoot, "src", "FSharp.MCP.DevKit.Server", "akka.server.conf")

    static member private getFreePort () =
        use listener = new TcpListener(System.Net.IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> System.Net.IPEndPoint).Port
        listener.Stop()
        port

    static member private requireFile path =
        if not (File.Exists path) then
            invalidOp $"Required test artifact was not found: {path}"

    static member private appendLine (builder: StringBuilder) (line: string) =
        lock builder (fun () -> builder.AppendLine(line) |> ignore)

    static member private waitUntil (timeoutMs: int) (pollMs: int) (operation: unit -> Task<'T option>) : Task<'T> =
        task {
            let started = DateTime.UtcNow
            let mutable result = None

            while result.IsNone && (DateTime.UtcNow - started).TotalMilliseconds < float timeoutMs do
                do! Task.Delay(pollMs)
                let! current = operation()
                result <- current

            match result with
            | Some value -> return value
            | None -> return raise (InvalidOperationException($"Timed out after {timeoutMs}ms."))
        }

    static member private startProcSupervisor() =
        task {
            RealNet10HostIsolationTests.requireFile RealNet10HostIsolationTests.ServerRuntimeConfigPath
            RealNet10HostIsolationTests.requireFile RealNet10HostIsolationTests.ServerDepsPath
            RealNet10HostIsolationTests.requireFile RealNet10HostIsolationTests.ProcSupervisorDllPath
            RealNet10HostIsolationTests.requireFile RealNet10HostIsolationTests.AkkaConfigPath

            let systemName = $"proc-system-{Guid.NewGuid():N}"
            let supervisorPort = RealNet10HostIsolationTests.getFreePort ()
            let webPort = RealNet10HostIsolationTests.getFreePort ()
            let stdout = StringBuilder()
            let stderr = StringBuilder()

            let startInfo = ProcessStartInfo("dotnet")
            startInfo.WorkingDirectory <- RealNet10HostIsolationTests.ServerOutputDir
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true

            [ "exec"
              "--runtimeconfig"
              RealNet10HostIsolationTests.ServerRuntimeConfigPath
              "--depsfile"
              RealNet10HostIsolationTests.ServerDepsPath
              RealNet10HostIsolationTests.ProcSupervisorDllPath
              "--mode"
              "supervisor"
              "--systemname"
              systemName
              "--host"
              "127.0.0.1"
              "--port"
              string supervisorPort
              "--webhost"
              "127.0.0.1"
              "--webport"
              string webPort
              "--spawnnone" ]
            |> List.iter startInfo.ArgumentList.Add

            let proc = new Process()
            proc.StartInfo <- startInfo
            proc.OutputDataReceived.Add(fun args ->
                if not (isNull args.Data) then
                    RealNet10HostIsolationTests.appendLine stdout args.Data)
            proc.ErrorDataReceived.Add(fun args ->
                if not (isNull args.Data) then
                    RealNet10HostIsolationTests.appendLine stderr args.Data)

            if not (proc.Start()) then
                invalidOp "Failed to start ProcSupervisor process."

            proc.BeginOutputReadLine()
            proc.BeginErrorReadLine()

            use httpClient = new HttpClient()

            try
                let! (_: unit) =
                    RealNet10HostIsolationTests.waitUntil 15000 250 (fun () ->
                        task {
                            if proc.HasExited then
                                let details =
                                    $"ProcSupervisor exited early with code {proc.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}"

                                raise (InvalidOperationException details)

                            try
                                let! response = httpClient.GetAsync($"http://127.0.0.1:{webPort}/health")

                                if response.IsSuccessStatusCode then
                                    return Some()
                                else
                                    return None
                            with _ ->
                                return None
                        })
                ()

                let clientConfig =
                    File.ReadAllText(RealNet10HostIsolationTests.AkkaConfigPath)
                    |> ConfigurationFactory.ParseString

                let actorSystem = ActorSystem.Create($"McpTestClient-{Guid.NewGuid():N}", clientConfig)
                let supervisorPath = $"akka.tcp://{systemName}@127.0.0.1:{supervisorPort}/user/proc-supervisor"
                let procClient = ProcSupervisorClient(actorSystem, supervisorPath, TimeSpan.FromSeconds 10.0) :> IProcSupervisorClient
                let fsiClient = FsiSupervisorClient(actorSystem, TimeSpan.FromSeconds 30.0) :> IFsiSupervisorClient

                let! (_: unit) =
                    RealNet10HostIsolationTests.waitUntil 15000 250 (fun () ->
                        task {
                            try
                                let! _ = procClient.ListProcInfo()
                                return Some()
                            with _ ->
                                return None
                        })
                ()

                return proc, stdout, stderr, actorSystem, procClient, fsiClient, systemName, supervisorPort
            with ex ->
                try
                    if not proc.HasExited then
                        proc.Kill(true)
                        proc.WaitForExit(5000) |> ignore
                with _ -> ()

                return raise ex
        }

    static member private createProcNodeArguments procId systemName supervisorPort childPort =
        String.Join(
            Environment.NewLine,
            [ "exec"
              "--runtimeconfig"
              RealNet10HostIsolationTests.ServerRuntimeConfigPath
              "--depsfile"
              RealNet10HostIsolationTests.ServerDepsPath
              RealNet10HostIsolationTests.ProcSupervisorDllPath
              "--mode"
              "procnode"
              "--procid"
              procId
              "--systemname"
              systemName
              "--supervisor"
              $"akka.tcp://{systemName}@127.0.0.1:{supervisorPort}/user/proc-supervisor"
              "--seed"
              $"akka.tcp://{systemName}@127.0.0.1:{supervisorPort}"
              "--host"
              "127.0.0.1"
              "--port"
              string childPort ]
        )

    static member private withRealNet10Service testBody =
        task {
            let mutable procOpt : Process option = None
            let mutable actorSystemOpt : ActorSystem option = None
            let mutable procClientOpt : IProcSupervisorClient option = None

            try
                let! proc, _, _, actorSystem, procClient, fsiClient, systemName, supervisorPort = RealNet10HostIsolationTests.startProcSupervisor()
                procOpt <- Some proc
                actorSystemOpt <- Some actorSystem
                procClientOpt <- Some procClient

                use service =
                    new FsiMcpService(
                        NullLogger<FsiMcpService>.Instance,
                        enableRemoteClient = false,
                        procSupervisorClient = procClient,
                        fsiSupervisorClient = fsiClient
                    )

                return! testBody service procClient systemName supervisorPort
            finally
                match procClientOpt with
                | Some procClient ->
                    try
                        let snapshots = procClient.ListProcInfo().GetAwaiter().GetResult()

                        for snapshot in snapshots do
                            try
                            procClient.StopProc(snapshot.ProcId, true).GetAwaiter().GetResult() |> ignore
                            with _ -> ()
                    with _ -> ()
                | None -> ()

                match actorSystemOpt with
                | Some actorSystem ->
                    try
                        actorSystem.Terminate().GetAwaiter().GetResult() |> ignore
                    with _ -> ()
                | None -> ()

                match procOpt with
                | Some proc ->
                    try
                        if not proc.HasExited then
                            proc.Kill(true)
                            proc.WaitForExit(5000) |> ignore
                    with _ -> ()
                    proc.Dispose()
                | None -> ()
        }

    static member private waitForHostReady (procClient: IProcSupervisorClient) hostId : Task<ProcHostSnapshot> =
        RealNet10HostIsolationTests.waitUntil 15000 250 (fun () ->
            task {
                let! snapshotOpt = procClient.GetProcInfo(hostId)

                match snapshotOpt with
                | Some snapshot when String.Equals(snapshot.Status, "running", StringComparison.OrdinalIgnoreCase)
                                    && snapshot.FsiSupervisorPath.IsSome ->
                    return Some snapshot
                | _ ->
                    return None
            })

    static member private retryTask (timeoutMs: int) (pollMs: int) (operation: unit -> Task<'T>) : Task<'T> =
        task {
            let started = DateTime.UtcNow
            let mutable lastError : exn option = None
            let mutable result : 'T option = None

            while result.IsNone && (DateTime.UtcNow - started).TotalMilliseconds < float timeoutMs do
                try
                    let! value = operation()
                    result <- Some value
                with ex ->
                    lastError <- Some ex
                    do! Task.Delay(pollMs)

            match result with
            | Some value -> return value
            | None ->
                return
                    match lastError with
                    | Some ex -> raise ex
                    | None -> raise (InvalidOperationException($"Timed out after {timeoutMs}ms."))
        }

    [<Fact>]
    member _.``Real out-of-proc net10 hosts keep state isolated``() =
        RealNet10HostIsolationTests.withRealNet10Service (fun service procClient systemName supervisorPort ->
            task {
                let agentId = "real-host-agent"
                let hostAId = "real-host-a"
                let hostBId = "real-host-b"
                let sessionId = "shared-session"

                let _ = McpControlPlaneTools.RegisterFsiAgent(service, agentId, "Real Host Agent")

                let! _ =
                    McpControlPlaneTools.CreateFsiHost(
                        service,
                        agentId,
                        "net10",
                        "dotnet",
                        RealNet10HostIsolationTests.createProcNodeArguments
                            hostAId
                            systemName
                            supervisorPort
                            (RealNet10HostIsolationTests.getFreePort ()),
                        RealNet10HostIsolationTests.ServerOutputDir,
                        hostAId,
                        "PING",
                        1000
                    )

                let! _ =
                    McpControlPlaneTools.CreateFsiHost(
                        service,
                        agentId,
                        "net10",
                        "dotnet",
                        RealNet10HostIsolationTests.createProcNodeArguments
                            hostBId
                            systemName
                            supervisorPort
                            (RealNet10HostIsolationTests.getFreePort ()),
                        RealNet10HostIsolationTests.ServerOutputDir,
                        hostBId,
                        "PING",
                        1000
                    )

                let! _ = RealNet10HostIsolationTests.waitForHostReady procClient hostAId
                let! _ = RealNet10HostIsolationTests.waitForHostReady procClient hostBId

                let hosts =
                    McpControlPlaneTools.ListFsiHosts(service, agentId)
                    |> FSharpJson.deserialize<HostRecord list>

                Assert.True(hosts |> List.exists (fun host -> host.HostId = hostAId && host.Status = Ready))
                Assert.True(hosts |> List.exists (fun host -> host.HostId = hostBId && host.Status = Ready))

                let! _ =
                    RealNet10HostIsolationTests.retryTask 15000 250 (fun () ->
                        McpControlPlaneTools.CreateFsiSession(service, agentId, hostAId, sessionId, "Host A Session"))

                let! _ =
                    RealNet10HostIsolationTests.retryTask 15000 250 (fun () ->
                        McpControlPlaneTools.CreateFsiSession(service, agentId, hostBId, sessionId, "Host B Session"))

                let! _ =
                    RealNet10HostIsolationTests.retryTask 15000 250 (fun () ->
                        McpExecutionTools.ExecuteFSharpCodeRouted(service, agentId, hostAId, sessionId, "let hostValue = 101", 30))

                let! _ =
                    RealNet10HostIsolationTests.retryTask 15000 250 (fun () ->
                        McpExecutionTools.ExecuteFSharpCodeRouted(service, agentId, hostBId, sessionId, "let hostValue = 202", 30))

                let! valueA =
                    RealNet10HostIsolationTests.retryTask 15000 250 (fun () ->
                        McpExecutionTools.EvaluateFSharpExpressionRouted(service, agentId, hostAId, sessionId, "hostValue", 30))

                let! valueB =
                    RealNet10HostIsolationTests.retryTask 15000 250 (fun () ->
                        McpExecutionTools.EvaluateFSharpExpressionRouted(service, agentId, hostBId, sessionId, "hostValue", 30))

                let sessionsA =
                    McpControlPlaneTools.ListFsiSessions(service, hostAId)
                    |> FSharpJson.deserialize<SessionRecord list>

                let sessionsB =
                    McpControlPlaneTools.ListFsiSessions(service, hostBId)
                    |> FSharpJson.deserialize<SessionRecord list>

                Assert.Equal("101", valueA)
                Assert.Equal("202", valueB)
                Assert.True(sessionsA |> List.exists (fun session -> session.SessionId = sessionId && session.Status = SessionReady))
                Assert.True(sessionsB |> List.exists (fun session -> session.SessionId = sessionId && session.Status = SessionReady))
            })

    [<Fact>]
    member _.``Real out-of-proc net10 host executes multi-interaction batches``() =
        RealNet10HostIsolationTests.withRealNet10Service (fun service procClient systemName supervisorPort ->
            task {
                let agentId = "real-batch-agent"
                let hostId = "real-batch-host"
                let sessionId = "batch-session"

                let _ = McpControlPlaneTools.RegisterFsiAgent(service, agentId, "Real Batch Agent")

                let! _ =
                    McpControlPlaneTools.CreateFsiHost(
                        service,
                        agentId,
                        "net10",
                        "dotnet",
                        RealNet10HostIsolationTests.createProcNodeArguments
                            hostId
                            systemName
                            supervisorPort
                            (RealNet10HostIsolationTests.getFreePort ()),
                        RealNet10HostIsolationTests.ServerOutputDir,
                        hostId,
                        "PING",
                        1000
                    )

                let! _ = RealNet10HostIsolationTests.waitForHostReady procClient hostId

                let! _ =
                    RealNet10HostIsolationTests.retryTask 15000 250 (fun () ->
                        McpControlPlaneTools.CreateFsiSession(service, agentId, hostId, sessionId, "Batch Session"))

                let! defineResult =
                    RealNet10HostIsolationTests.retryTask 15000 250 (fun () ->
                        McpExecutionTools.ExecuteFSharpCodeRouted(
                            service,
                            agentId,
                            hostId,
                            sessionId,
                            "let firstValue = 1;;\nlet secondValue = 2",
                            30
                        ))

                let! value =
                    RealNet10HostIsolationTests.retryTask 15000 250 (fun () ->
                        McpExecutionTools.EvaluateFSharpExpressionRouted(service, agentId, hostId, sessionId, "secondValue", 30))

                Assert.DoesNotContain("Execution failed", defineResult)
                Assert.Equal("2", value)
            })
