module SmokeRegressionTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.Integration
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Server.ResultQuery

type private SessionState =
    { Variables: ConcurrentDictionary<string, string>
      Refs: ResizeArray<string>
      Loads: ResizeArray<string>
      SearchPaths: ResizeArray<string>
      RunningSinceUtc: DateTime }

type private StatefulFakeProcSupervisorClient() =
    let snapshots = ConcurrentDictionary<string, ProcHostSnapshot>()

    interface IProcSupervisorClient with
        member _.StartProc(procId: string, spec: ProcHostSpec) =
            let snapshot =
                { ProcId = procId
                  Status = "running"
                  ProcessId = Some(8000 + snapshots.Count)
                  FsiSupervisorPath = Some $"akka.tcp://FsiExecutionSystem@localhost:{9000 + snapshots.Count}/user/fsi/supervisor"
                  NodeAddress = Some $"akka.tcp://FsiExecutionSystem@localhost:{9000 + snapshots.Count}"
                  LastProbeUtc = Some DateTime.UtcNow
                  LastProbeOk = Some true
                  ProbeFailures = 0
                  Spec = Some spec
                  LastError = None }

            snapshots.[procId] <- snapshot
            Task.FromResult snapshot

        member _.StopProc(procId: string, _) =
            let snapshot =
                snapshots.[procId]

            let stopped =
                { snapshot with
                    Status = "stopped"
                    LastProbeUtc = Some DateTime.UtcNow }

            snapshots.[procId] <- stopped
            Task.FromResult stopped

        member _.GetProcInfo(procId: string) =
            Task.FromResult(
                match snapshots.TryGetValue procId with
                | true, snapshot -> Some snapshot
                | false, _ -> None
            )

        member _.ListProcInfo() = Task.FromResult(snapshots.Values |> Seq.toList)

        member _.RestartProc(procId: string) =
            let snapshot = snapshots.[procId]

            let restarted =
                { snapshot with
                    Status = "running"
                    LastProbeUtc = Some DateTime.UtcNow
                    LastProbeOk = Some true
                    ProbeFailures = 0 }

            snapshots.[procId] <- restarted
            Task.FromResult restarted

type private StatefulFakeFsiSupervisorClient() =
    let sessions = ConcurrentDictionary<string * string, SessionState>()

    let getOrCreateSession hostId sessionId =
        sessions.GetOrAdd(
            (hostId, sessionId),
            fun _ ->
                { Variables = ConcurrentDictionary()
                  Refs = ResizeArray()
                  Loads = ResizeArray()
                  SearchPaths = ResizeArray()
                  RunningSinceUtc = DateTime.UtcNow }
        )

    let tryGetSession hostId sessionId =
        match sessions.TryGetValue((hostId, sessionId)) with
        | true, state -> Some state
        | false, _ -> None

    let trimQuotes (value: string) =
        let trimmed = value.Trim()

        if trimmed.Length >= 2 && trimmed.StartsWith("\"") && trimmed.EndsWith("\"") then
            trimmed.Substring(1, trimmed.Length - 2)
        else
            trimmed

    let executeCode (hostId: string) (request: FsiSupervisorExecRequest) =
        let state = getOrCreateSession hostId request.SessionId

        request.Refs |> List.iter state.Refs.Add
        request.Loads |> List.iter state.Loads.Add

        let code = request.Code.Trim()

        if String.IsNullOrWhiteSpace code || code = "()" then
            { SessionId = request.SessionId
              RawErrorType = None
              Result = { FsiResult.empty with Output = "ok"; Value = Some "ok" } }
        elif code.StartsWith("#I ", StringComparison.Ordinal) then
            let path = code.Substring(3) |> trimQuotes
            state.SearchPaths.Add path

            { SessionId = request.SessionId
              RawErrorType = None
              Result = { FsiResult.empty with Output = path; Value = Some path } }
        elif code.StartsWith("#r ", StringComparison.Ordinal) then
            let reference = code.Substring(3) |> trimQuotes
            state.Refs.Add reference

            { SessionId = request.SessionId
              RawErrorType = None
              Result = { FsiResult.empty with Output = reference; Value = Some reference } }
        elif code.StartsWith("let ", StringComparison.Ordinal) then
            let body = code.Substring(4)
            let parts = body.Split([| '=' |], 2)

            if parts.Length = 2 then
                let name = parts.[0].Trim().Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> Array.last
                let value = parts.[1].Trim() |> trimQuotes
                state.Variables.[name] <- value

                { SessionId = request.SessionId
                  RawErrorType = None
                  Result = { FsiResult.empty with Output = code; Value = Some value } }
            else
                { SessionId = request.SessionId
                  RawErrorType = Some "ParseError"
                  Result =
                    { Output = ""
                      Errors = "Unsupported let binding format."
                      IsSuccess = false
                      ExecutionTime = None
                      Diagnostics = [||]
                      Value = None } }
        else
            match state.Variables.TryGetValue code with
            | true, value ->
                { SessionId = request.SessionId
                  RawErrorType = None
                  Result = { FsiResult.empty with Output = value; Value = Some value } }
            | false, _ ->
                { SessionId = request.SessionId
                  RawErrorType = Some "MissingValue"
                  Result =
                    { Output = ""
                      Errors = $"Value '{code}' is not defined."
                      IsSuccess = false
                      ExecutionTime = None
                      Diagnostics = [||]
                      Value = None } }

    interface IFsiSupervisorClient with
        member _.Execute(host: HostRecord, request: FsiSupervisorExecRequest) =
            Task.FromResult(executeCode host.HostId request)

        member _.GetSessionInfo(host: HostRecord, sessionId: string) =
            Task.FromResult(
                match tryGetSession host.HostId sessionId with
                | Some state ->
                    { SessionId = sessionId
                      Status = "ready"
                      Refs = List.ofSeq state.Refs
                      Loads = List.ofSeq state.Loads
                      SearchPaths = List.ofSeq state.SearchPaths
                      Variables = state.Variables |> Seq.map (fun pair -> pair.Key, pair.Value) |> Seq.toList
                      LastCheckpointId = None
                      RunningSinceUtc = Some state.RunningSinceUtc }
                | None ->
                    { SessionId = sessionId
                      Status = "missing"
                      Refs = []
                      Loads = []
                      SearchPaths = []
                      Variables = []
                      LastCheckpointId = None
                      RunningSinceUtc = None }
            )

        member _.ListSessions(host: HostRecord) =
            Task.FromResult(
                sessions
                |> Seq.choose (fun pair ->
                    let (currentHostId, sessionId) = pair.Key

                    if currentHostId = host.HostId then
                        let state = pair.Value

                        Some
                            { SessionId = sessionId
                              Status = "ready"
                              Refs = List.ofSeq state.Refs
                              Loads = List.ofSeq state.Loads
                              SearchPaths = List.ofSeq state.SearchPaths
                              Variables = state.Variables |> Seq.map (fun item -> item.Key, item.Value) |> Seq.toList
                              LastCheckpointId = None
                              RunningSinceUtc = Some state.RunningSinceUtc }
                    else
                        None)
                |> Seq.toList
            )

        member _.ResetSession(host: HostRecord, sessionId: string) =
            let existed = sessions.TryRemove((host.HostId, sessionId)) |> fst

            Task.FromResult(
                { SessionId = sessionId
                  Existed = existed
                  Status = if existed then "reset" else "missing" }
            )

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

let private createNet10SmokeService () =
    let procClient = StatefulFakeProcSupervisorClient() :> IProcSupervisorClient
    let fsiClient = StatefulFakeFsiSupervisorClient() :> IFsiSupervisorClient

    new FsiMcpService(
        NullLogger<FsiMcpService>.Instance,
        enableRemoteClient = false,
        procSupervisorClient = procClient,
        fsiSupervisorClient = fsiClient
    )

[<Fact>]
let ``Smoke old tools remain compatible on default route`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! execOutput = FSharpInteractiveTools.ExecuteFSharpCode(service, "let legacySmoke = 5", 30)
        let! evalBeforeRestart = FSharpInteractiveTools.EvaluateFSharpExpression(service, "legacySmoke", 30)
        let! _ = FSharpInteractiveTools.ResetFSISession(service, 30)
        let! evalAfterRestart = FSharpInteractiveTools.EvaluateFSharpExpression(service, "legacySmoke", 30)
        let! asyncId = FSharpInteractiveTools.ExecuteFSharpCodeAsync(service, "let legacySmoke = 9", 30)
        let! asyncStatus = waitForCompletion service asyncId
        let! evalAfterAsync = FSharpInteractiveTools.EvaluateFSharpExpression(service, "legacySmoke", 30)

        Assert.Contains("legacySmoke", execOutput)
        Assert.Equal("5", evalBeforeRestart)
        Assert.Contains("not defined", evalAfterRestart, StringComparison.OrdinalIgnoreCase)
        Assert.True(asyncStatus.IsCompleted)
        Assert.True(asyncStatus.ResultId.IsSome)
        Assert.Equal("9", evalAfterAsync)
    }

[<Fact>]
let ``Smoke multi-host routed execution keeps host state isolated`` () =
    task {
        let service = createNet10SmokeService ()
        use _cleanup = service :> IDisposable

        let _ = McpControlPlaneTools.RegisterFsiAgent(service, "agent-mh", "Multi Host Agent")

        let! _ = McpControlPlaneTools.CreateFsiHost(service, "agent-mh", "net10", "dotnet", "", "/srv/fsi", "host-a", "PING", 1000)
        let! _ = McpControlPlaneTools.CreateFsiHost(service, "agent-mh", "net10", "dotnet", "", "/srv/fsi", "host-b", "PING", 1000)
        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-mh", "host-a", "session-shared", "A")
        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-mh", "host-b", "session-shared", "B")

        let execA = McpExecutionTools.ExecuteFSharpCodeRouted(service, "agent-mh", "host-a", "session-shared", "let hostValue = 101", 30)
        let execB = McpExecutionTools.ExecuteFSharpCodeRouted(service, "agent-mh", "host-b", "session-shared", "let hostValue = 202", 30)
        let! _ = Task.WhenAll [| execA; execB |]

        let evalA = McpExecutionTools.EvaluateFSharpExpressionRouted(service, "agent-mh", "host-a", "session-shared", "hostValue", 30)
        let evalB = McpExecutionTools.EvaluateFSharpExpressionRouted(service, "agent-mh", "host-b", "session-shared", "hostValue", 30)
        let! values = Task.WhenAll(evalA, evalB)

        Assert.Equal("101", values.[0])
        Assert.Equal("202", values.[1])
    }

[<Fact>]
let ``Smoke multi-session routed execution keeps session state isolated`` () =
    task {
        let service = createNet10SmokeService ()
        use _cleanup = service :> IDisposable

        let _ = McpControlPlaneTools.RegisterFsiAgent(service, "agent-ms", "Multi Session Agent")

        let! _ = McpControlPlaneTools.CreateFsiHost(service, "agent-ms", "net10", "dotnet", "", "/srv/fsi", "host-ms", "PING", 1000)
        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-ms", "host-ms", "session-a", "Session A")
        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-ms", "host-ms", "session-b", "Session B")

        let! _ = McpExecutionTools.ExecuteFSharpCodeRouted(service, "agent-ms", "host-ms", "session-a", "let sessionValue = 11", 30)
        let! _ = McpExecutionTools.ExecuteFSharpCodeRouted(service, "agent-ms", "host-ms", "session-b", "let sessionValue = 22", 30)
        let! valueA = McpExecutionTools.EvaluateFSharpExpressionRouted(service, "agent-ms", "host-ms", "session-a", "sessionValue", 30)
        let! valueB = McpExecutionTools.EvaluateFSharpExpressionRouted(service, "agent-ms", "host-ms", "session-b", "sessionValue", 30)

        Assert.Equal("11", valueA)
        Assert.Equal("22", valueB)
    }

[<Fact>]
let ``Smoke net10 routed reset clears session state`` () =
    task {
        let service = createNet10SmokeService ()
        use _cleanup = service :> IDisposable

        let _ = McpControlPlaneTools.RegisterFsiAgent(service, "agent-reset", "Reset Agent")

        let! _ = McpControlPlaneTools.CreateFsiHost(service, "agent-reset", "net10", "dotnet", "", "/srv/fsi", "host-reset", "PING", 1000)
        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-reset", "host-reset", "session-reset", "Session Reset")

        let! _ = McpExecutionTools.ExecuteFSharpCodeRouted(service, "agent-reset", "host-reset", "session-reset", "let resetValue = 33", 30)
        let! beforeReset = McpExecutionTools.EvaluateFSharpExpressionRouted(service, "agent-reset", "host-reset", "session-reset", "resetValue", 30)
        let! resetOutput = McpExecutionTools.ResetFsiSessionRouted(service, "agent-reset", "host-reset", "session-reset", 30)
        let! afterReset = McpExecutionTools.EvaluateFSharpExpressionRouted(service, "agent-reset", "host-reset", "session-reset", "resetValue", 30)

        Assert.Equal("33", beforeReset)
        Assert.Equal("FSI session reset successfully", resetOutput)
        Assert.Contains("not defined", afterReset, StringComparison.OrdinalIgnoreCase)
    }

[<Fact>]
let ``Smoke async queue remains FIFO and links result ids`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let asyncId1 = service.EnqueueExecuteCode("let mutable fifoCounter = 1", TimeSpan.FromSeconds 30.0)
        let asyncId2 = service.EnqueueExecuteCode("fifoCounter <- fifoCounter + 1", TimeSpan.FromSeconds 30.0)

        let! status1 = waitForCompletion service asyncId1
        let! status2 = waitForCompletion service asyncId2
        let! evalRecord = service.ExecuteOperation(EvaluateExpression, "fifoCounter", timeout = TimeSpan.FromSeconds 30.0)

        Assert.True(status1.IsCompleted)
        Assert.True(status2.IsCompleted)
        Assert.True(status1.ResultId.IsSome)
        Assert.True(status2.ResultId.IsSome)
        Assert.Equal(Some "2", evalRecord.Result.Value)
    }

[<Fact>]
let ``Smoke result queries support exists forall and compare`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! _ = service.ExecuteOperation(ExecuteCode, "let querySmoke = 7", timeout = TimeSpan.FromSeconds 30.0)
        let! first = service.ExecuteOperation(EvaluateExpression, "querySmoke", timeout = TimeSpan.FromSeconds 30.0)
        let! _ = service.ExecuteOperation(ExecuteCode, "let querySmoke = 8", timeout = TimeSpan.FromSeconds 30.0)
        let! second = service.ExecuteOperation(EvaluateExpression, "querySmoke", timeout = TimeSpan.FromSeconds 30.0)

        let existsJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "exists",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "valuecontains:8",
                "",
                ""
            )

        let forallJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "forall",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "isSuccess",
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

        let existsResponse = FSharpJson.deserialize<ResultQueryResponse> existsJson
        let forallResponse = FSharpJson.deserialize<ResultQueryResponse> forallJson
        let compareResponse = FSharpJson.deserialize<ResultQueryResponse> compareJson

        Assert.True(existsResponse.IsSuccess)
        Assert.Equal("True", existsResponse.Output)
        Assert.True(forallResponse.IsSuccess)
        Assert.Equal("True", forallResponse.Output)
        Assert.True(compareResponse.IsSuccess)
        Assert.Contains(first.ResultId, compareResponse.MaterializedJson.Value)
        Assert.Contains(second.ResultId, compareResponse.MaterializedJson.Value)
    }
