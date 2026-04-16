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
          UsePackageTargets = None
          Metadata = Map.empty }

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
    Assert.Equal("agent-a", record.Metadata.[PrincipalAttribution.PrincipalId])
    Assert.Equal("agent", record.Metadata.[PrincipalAttribution.PrincipalKind])
    Assert.Equal("route", record.Metadata.[PrincipalAttribution.PrincipalSource])
    Assert.Equal("agent-a", record.Metadata.[PrincipalAttribution.ExecutionAgentId])
    Assert.Equal("host-a", record.Metadata.[PrincipalAttribution.ExecutionHostId])
    Assert.Equal("session-a", record.Metadata.[PrincipalAttribution.ExecutionSessionId])

[<Fact>]
let ``toExecutionRecord preserves explicit principal attribution metadata`` () =
    let request =
        { RequestId = "req-principal-1"
          Route =
            { AgentId = "agent-service"
              HostId = "host-service"
              SessionId = "session-service" }
          OperationKind = ExecuteCode
          Payload = "let value = 1"
          Timeout = Some(TimeSpan.FromSeconds 30.0)
          UsePackageTargets = None
          Metadata =
            [ PrincipalAttribution.PrincipalId, "human-admin"
              PrincipalAttribution.PrincipalKind, "human"
              PrincipalAttribution.PrincipalSource, "mgmt2" ]
            |> Map.ofList }

    let result =
        { Output = ""
          Errors = ""
          IsSuccess = true
          ExecutionTime = None
          Diagnostics = [||]
          Value = Some "1" }

    let now = DateTime.UtcNow

    let record =
        BackendAdapters.toExecutionRecord InProc request now (Some now) (Some now) "host-service" "session-service" "result-principal-1" result None

    Assert.Equal("human-admin", record.Metadata.[PrincipalAttribution.PrincipalId])
    Assert.Equal("human", record.Metadata.[PrincipalAttribution.PrincipalKind])
    Assert.Equal("mgmt2", record.Metadata.[PrincipalAttribution.PrincipalSource])
    Assert.Equal("agent-service", record.Metadata.[PrincipalAttribution.PrincipalAgentId])
    Assert.Equal("host-service", record.Metadata.[PrincipalAttribution.PrincipalHostId])
    Assert.Equal("session-service", record.Metadata.[PrincipalAttribution.PrincipalSessionId])

[<Fact>]
let ``toExecutionRecord preserves and normalizes browser-aware execution metadata`` () =
    let request =
        { RequestId = "req-browser-1"
          Route =
            { AgentId = "agent-browser"
              HostId = "host-browser"
              SessionId = "session-browser" }
          OperationKind = ExecuteCode
          Payload = "sbmgr |> ignore"
          Timeout = Some(TimeSpan.FromSeconds 30.0)
          UsePackageTargets = None
          Metadata =
            [ "schedule.target.kind", "tab"
              "schedule.target.browserId", "browser-01"
              "schedule.target.tabId", "tab-02"
              "schedule.target.companion.sessionId", "session-browser"
              "schedule.target.companion.hostId", "host-browser"
              "schedule.target.executionPlane", "remote-fsi"
              "custom.traceId", "trace-01" ]
            |> Map.ofList }

    let result =
        { Output = "ok"
          Errors = ""
          IsSuccess = true
          ExecutionTime = Some(TimeSpan.FromMilliseconds 5.0)
          Diagnostics = [||]
          Value = Some "unit" }

    let now = DateTime.UtcNow

    let record =
        BackendAdapters.toExecutionRecord
            Net10Remote
            request
            now
            (Some now)
            (Some now)
            "host-browser"
            "session-browser"
            "result-browser-1"
            result
            None

    Assert.Equal("tab", record.Metadata.[BackendAdapters.BrowserExecutionMetadata.TargetKind])
    Assert.Equal("browser-01", record.Metadata.[BackendAdapters.BrowserExecutionMetadata.BrowserId])
    Assert.Equal("tab-02", record.Metadata.[BackendAdapters.BrowserExecutionMetadata.TabId])
    Assert.Equal("session-browser", record.Metadata.[BackendAdapters.BrowserExecutionMetadata.CompanionSessionId])
    Assert.Equal("host-browser", record.Metadata.[BackendAdapters.BrowserExecutionMetadata.CompanionHostId])
    Assert.Equal("remote-fsi", record.Metadata.[BackendAdapters.BrowserExecutionMetadata.ExecutionPlane])
    Assert.Equal("trace-01", record.Metadata.["custom.traceId"])
    Assert.Equal("browser-01", record.Metadata.["schedule.target.browserId"])
