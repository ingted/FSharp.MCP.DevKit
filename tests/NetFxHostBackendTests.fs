module NetFxHostBackendTests

open System
open System.Threading.Tasks
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Messages
open FSharp.MCP.DevKit.Server.Backends

type private FakeRemoteFsiClient(responseFactory: RemoteFsiCommand -> FsiRemoteCommandResponse) =
    let mutable commands : RemoteFsiCommand list = []

    member _.Commands = List.rev commands

    interface IRemoteFsiClient with
        member _.SendCommand(command: RemoteFsiCommand) =
            commands <- command :: commands
            Task.FromResult(responseFactory command)

        member _.IsServerAvailable() = true

let private createSuccessResult output =
    { Output = output
      Errors = ""
      IsSuccess = true
      ExecutionTimeMs = Some 5.0
      Diagnostics = [||]
      Value = None
      RawErrorType = None }

[<Fact>]
let ``NetFxHostBackend forwards execute requests with explicit route`` () =
    task {
        let fakeClient =
            FakeRemoteFsiClient(fun command ->
                { RequestId = "remote-1"
                  HostId = command.Route |> Option.map (fun route -> route.HostId)
                  SessionId = command.Route |> Option.map (fun route -> route.SessionId)
                  Result = createSuccessResult "ok"
                  SessionState =
                    Some
                        { SessionId = "session-1"
                          SessionName = "session-1"
                          Status = "SessionReady"
                          Refs = []
                          Loads = []
                          SearchPaths = []
                          Variables = []
                          LastCheckpointId = None
                          RunningSinceUtc = Some DateTime.UtcNow
                          LastExecutionAt = Some DateTime.UtcNow } })

        let backend = NetFxHostBackend(fakeClient :> IRemoteFsiClient) :> IFsiExecutionBackend

        let route : ExecutionRoute =
            { AgentId = "agent-1"
              HostId = "host-1"
              SessionId = "session-1" }

        let! record =
            backend.Execute(
                { RequestId = "req-1"
                  Route = route
                  OperationKind = ExecuteCode
                  Payload = "let x = 1"
                  Timeout = Some(TimeSpan.FromSeconds 30.0)
                  UsePackageTargets = None }
            )

        let command = fakeClient.Commands |> List.head

        Assert.Equal("EXEC", command.CommandType)
        Assert.Equal(Some "agent-1", command.Route |> Option.map (fun value -> value.AgentId))
        Assert.Equal(Some "host-1", command.Route |> Option.map (fun value -> value.HostId))
        Assert.Equal(Some "session-1", command.Route |> Option.map (fun value -> value.SessionId))
        Assert.True(record.Result.IsSuccess)
        Assert.Equal(NetFxRemote, record.BackendKind)
        Assert.Equal("host-1", record.HostId)
        Assert.Equal("session-1", record.SessionId)
    }

[<Fact>]
let ``NetFxHostBackend maps remote session snapshot into SessionRecord`` () =
    task {
        let fakeClient =
            FakeRemoteFsiClient(fun _ ->
                { RequestId = "remote-2"
                  HostId = Some "host-2"
                  SessionId = Some "session-2"
                  Result = createSuccessResult "state"
                  SessionState =
                    Some
                        { SessionId = "session-2"
                          SessionName = "Session Two"
                          Status = "SessionReady"
                          Refs = [ "a.dll" ]
                          Loads = [ "boot.fsx" ]
                          SearchPaths = [ "/tmp" ]
                          Variables = [ "v", "int" ]
                          LastCheckpointId = Some "cp-1"
                          RunningSinceUtc = Some DateTime.UtcNow
                          LastExecutionAt = Some DateTime.UtcNow } })

        let backend = NetFxHostBackend(fakeClient :> IRemoteFsiClient) :> IFsiExecutionBackend
        let route : ExecutionRoute =
            { AgentId = "agent-2"
              HostId = "host-2"
              SessionId = "session-2" }

        let! state = backend.GetSessionState(route)

        Assert.Equal("Session Two", state.SessionName)
        Assert.Equal(SessionReady, state.Status)
        Assert.Contains("a.dll", state.Refs)
        Assert.Contains("/tmp", state.SearchPaths)
        Assert.Equal(Some "cp-1", state.LastCheckpointId)
    }

[<Fact>]
let ``NetFxHostBackend maps result query and host commands to parent-level remote commands`` () =
    task {
        let fakeClient =
            FakeRemoteFsiClient(fun command ->
                { RequestId = "remote-3"
                  HostId = command.Route |> Option.map (fun route -> route.HostId)
                  SessionId = command.Route |> Option.map (fun route -> route.SessionId)
                  Result = createSuccessResult command.CommandType
                  SessionState = None })

        let backend = NetFxHostBackend(fakeClient :> IRemoteFsiClient) :> IFsiExecutionBackend
        let route : ExecutionRoute =
            { AgentId = "agent-3"
              HostId = "host-3"
              SessionId = "session-3" }

        let! _ =
            backend.Execute(
                { RequestId = "req-result-op"
                  Route = route
                  OperationKind = ResultQuery
                  Payload = "rid-1 rid-2"
                  Timeout = Some(TimeSpan.FromSeconds 30.0)
                  UsePackageTargets = None }
            )

        let host =
            { HostId = "host-3"
              AgentId = "agent-3"
              HostKind = NetFxHost
              BackendKind = NetFxRemote
              Status = Ready
              Address = None
              ProcId = None
              CreatedAt = DateTime.UtcNow
              LastHealthCheckAt = None
              LastError = None }

        do! backend.RestartHost(host)
        let! health = backend.HealthCheck(host)

        let commands = fakeClient.Commands

        Assert.Contains(commands, fun command -> command.CommandType = "RESULT_OP" && command.Route |> Option.exists (fun route -> route.SessionId = "session-3"))
        Assert.Contains(commands, fun command -> command.CommandType = "RESTART_HOST" && command.Route |> Option.exists (fun route -> route.SessionId = "host-control"))
        Assert.Contains(commands, fun command -> command.CommandType = "PING" && command.Route |> Option.exists (fun route -> route.SessionId = "host-control"))
        Assert.True(health.IsAvailable)
        Assert.Equal(Some "PING", health.Message)
    }

[<Fact>]
let ``NetFxHostBackend reset session uses RESET command with target route`` () =
    task {
        let fakeClient =
            FakeRemoteFsiClient(fun command ->
                { RequestId = "remote-4"
                  HostId = command.Route |> Option.map (fun route -> route.HostId)
                  SessionId = command.Route |> Option.map (fun route -> route.SessionId)
                  Result = createSuccessResult "reset"
                  SessionState = None })

        let backend = NetFxHostBackend(fakeClient :> IRemoteFsiClient) :> IFsiExecutionBackend
        let route : ExecutionRoute =
            { AgentId = "agent-4"
              HostId = "host-4"
              SessionId = "session-4" }

        let! result = backend.ResetSession(route)
        let command = fakeClient.Commands |> List.head

        Assert.Equal("RESET", command.CommandType)
        Assert.Equal(Some "session-4", command.Route |> Option.map (fun value -> value.SessionId))
        Assert.True(result.Result.IsSuccess)
    }
