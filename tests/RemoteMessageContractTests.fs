module RemoteMessageContractTests

open Xunit
open FSharp.MCP.DevKit.Messages

[<Fact>]
let ``FsiRemoteCommandRequest carries route and timeout`` () =
    let request =
        { RequestId = "req-1"
          CommandType = "EXEC"
          Payload = "printfn \"hi\""
          Route =
            Some
                { AgentId = Some "agent-1"
                  HostId = Some "host-1"
                  SessionId = Some "session-1" }
          UsePackageTargets = Some true
          TimeoutMs = Some 30000 }

    Assert.Equal(Some "agent-1", request.Route |> Option.bind (fun route -> route.AgentId))
    Assert.Equal(Some "host-1", request.Route |> Option.bind (fun route -> route.HostId))
    Assert.Equal(Some "session-1", request.Route |> Option.bind (fun route -> route.SessionId))
    Assert.Equal(Some 30000, request.TimeoutMs)

[<Fact>]
let ``FsiRemoteResult carries value and raw error type`` () =
    let result =
        { Output = ""
          Errors = "boom"
          IsSuccess = false
          ExecutionTimeMs = Some 12.0
          Diagnostics = [||]
          Value = Some "42"
          RawErrorType = Some "RemoteExecutionError" }

    Assert.Equal(Some "42", result.Value)
    Assert.Equal(Some "RemoteExecutionError", result.RawErrorType)

[<Fact>]
let ``FsiRemoteCommandResponse carries host and session ids`` () =
    let response =
        { RequestId = "req-2"
          HostId = Some "host-2"
          SessionId = Some "session-2"
          Result =
            { Output = "ok"
              Errors = ""
              IsSuccess = true
              ExecutionTimeMs = None
              Diagnostics = [||]
              Value = None
              RawErrorType = None }
          SessionState =
            Some
                { SessionId = "session-2"
                  SessionName = "session-2"
                  Status = "SessionReady"
                  Refs = []
                  Loads = []
                  SearchPaths = []
                  Variables = []
                  LastCheckpointId = None
                  RunningSinceUtc = None
                  LastExecutionAt = None } }

    Assert.Equal(Some "host-2", response.HostId)
    Assert.Equal(Some "session-2", response.SessionId)
    Assert.True(response.Result.IsSuccess)
    Assert.True(response.SessionState.IsSome)
