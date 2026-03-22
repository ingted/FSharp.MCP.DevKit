module BackendAdaptersTests

open System
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Communication.IPC
open FSharp.MCP.DevKit.Server.Backends

[<Fact>]
let ``toFsiResult maps diagnostics and value from pipe response`` () =
    let response =
        { RequestId = "req-1"
          IsSuccess = false
          Output = "stdout"
          Errors = "stderr"
          Diagnostics =
            Some
                [| { FileName = "test.fsx"
                     StartLine = 3
                     EndLine = 3
                     StartColumn = 4
                     EndColumn = 9
                     Severity = "Error"
                     Message = "boom" } |]
          Value = Some "42"
          ExecutionTime = Some(TimeSpan.FromMilliseconds 12.0)
          Timestamp = DateTime.UtcNow }

    let result = BackendAdapters.toFsiResult response

    Assert.False(result.IsSuccess)
    Assert.Equal("stdout", result.Output)
    Assert.Equal("stderr", result.Errors)
    Assert.Equal(Some "42", result.Value)
    Assert.Equal(1, result.Diagnostics.Length)
    Assert.Equal("Error", result.Diagnostics.[0].Severity)
    Assert.Equal("boom", result.Diagnostics.[0].Message)

[<Fact>]
let ``inferRawErrorType returns None for successful response`` () =
    let response =
        { RequestId = "req-2"
          IsSuccess = true
          Output = ""
          Errors = ""
          Diagnostics = None
          Value = None
          ExecutionTime = None
          Timestamp = DateTime.UtcNow }

    let rawErrorType = BackendAdapters.inferRawErrorType response
    Assert.Equal(None, rawErrorType)

[<Fact>]
let ``inferRawErrorType returns UnknownRemoteError for blank error text`` () =
    let response =
        { RequestId = "req-3"
          IsSuccess = false
          Output = ""
          Errors = "   "
          Diagnostics = None
          Value = None
          ExecutionTime = None
          Timestamp = DateTime.UtcNow }

    let rawErrorType = BackendAdapters.inferRawErrorType response
    Assert.Equal(Some "UnknownRemoteError", rawErrorType)

[<Fact>]
let ``inferRawErrorType returns RemoteExecutionError for explicit error text`` () =
    let response =
        { RequestId = "req-4"
          IsSuccess = false
          Output = ""
          Errors = "failed"
          Diagnostics = None
          Value = None
          ExecutionTime = None
          Timestamp = DateTime.UtcNow }

    let rawErrorType = BackendAdapters.inferRawErrorType response
    Assert.Equal(Some "RemoteExecutionError", rawErrorType)

[<Fact>]
let ``toExecutionRecord preserves routing and backend metadata`` () =
    let request =
        { RequestId = "req-5"
          Route =
            { AgentId = "agent-a"
              HostId = "host-a"
              SessionId = "session-a" }
          OperationKind = ExecuteCode
          Payload = "printfn \"hi\""
          Timeout = Some(TimeSpan.FromSeconds 30.0)
          UsePackageTargets = None }

    let result =
        { Output = "hi"
          Errors = ""
          IsSuccess = true
          ExecutionTime = Some(TimeSpan.FromMilliseconds 18.0)
          Diagnostics = [||]
          Value = Some "unit" }

    let submittedAt = DateTime.UtcNow
    let startedAt = Some(submittedAt.AddMilliseconds 1.0)
    let completedAt = Some(submittedAt.AddMilliseconds 20.0)

    let record =
        BackendAdapters.toExecutionRecord
            Net10Remote
            request
            submittedAt
            startedAt
            completedAt
            "host-z"
            "session-z"
            "result-z"
            result
            (Some "RemoteExecutionError")

    Assert.Equal("req-5", record.RequestId)
    Assert.Equal("agent-a", record.AgentId)
    Assert.Equal(Net10Remote, record.BackendKind)
    Assert.Equal("host-z", record.HostId)
    Assert.Equal("session-z", record.SessionId)
    Assert.Equal("result-z", record.ResultId)
    Assert.Equal(Some "RemoteExecutionError", record.RawErrorType)
    Assert.Equal("hi", record.Result.Output)
