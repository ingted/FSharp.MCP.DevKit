module InProcBackendTests

open System
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.Backends

let private route agentId hostId sessionId =
    { AgentId = agentId
      HostId = hostId
      SessionId = sessionId }

let private request route operation payload =
    { RequestId = Guid.NewGuid().ToString("N")
      Route = route
      OperationKind = operation
      Payload = payload
      Timeout = Some(TimeSpan.FromSeconds 30.0)
      UsePackageTargets = None }

[<Fact>]
let ``InProcBackend isolates state between sessions`` () =
    task {
        let backend = InProcBackend() :> IFsiExecutionBackend
        let routeA = route "agent-a" "default-host" "session-a"
        let routeB = route "agent-a" "default-host" "session-b"

        let! defineResult = backend.Execute(request routeA ExecuteCode "let sessionScopedValue = 42")
        let! evalSameSession = backend.Execute(request routeA EvaluateExpression "sessionScopedValue")
        let! evalOtherSession = backend.Execute(request routeB EvaluateExpression "sessionScopedValue")

        Assert.True(defineResult.Result.IsSuccess)
        Assert.True(evalSameSession.Result.IsSuccess)
        Assert.Equal(Some "42", evalSameSession.Result.Value)
        Assert.False(evalOtherSession.Result.IsSuccess)
    }

[<Fact>]
let ``InProcBackend reset clears session state`` () =
    task {
        let backend = InProcBackend() :> IFsiExecutionBackend
        let routeA = route "agent-a" "default-host" "session-reset"

        let! _ = backend.Execute(request routeA ExecuteCode "let resetValue = 99")
        let! beforeReset = backend.Execute(request routeA EvaluateExpression "resetValue")
        let! _ = backend.ResetSession(routeA)
        let! afterReset = backend.Execute(request routeA EvaluateExpression "resetValue")

        Assert.True(beforeReset.Result.IsSuccess)
        Assert.Equal(Some "99", beforeReset.Result.Value)
        Assert.False(afterReset.Result.IsSuccess)
    }

[<Fact>]
let ``InProcBackend returns execution metadata and session state`` () =
    task {
        let backend = InProcBackend() :> IFsiExecutionBackend
        let routeA = route "agent-a" "default-host" "session-meta"

        let! record = backend.Execute(request routeA AddSearchPath "/tmp")
        let! state = backend.GetSessionState(routeA)

        Assert.Equal(InProc, record.BackendKind)
        Assert.Equal("agent-a", record.AgentId)
        Assert.Equal("default-host", record.HostId)
        Assert.Equal("session-meta", record.SessionId)
        Assert.False(String.IsNullOrWhiteSpace(record.ResultId))
        Assert.Contains("/tmp", state.SearchPaths)
        Assert.Equal(SessionReady, state.Status)
    }
