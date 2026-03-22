module ExecutionRouterTests

open System
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.Backends
open FSharp.MCP.DevKit.Server.ControlPlane

let private createRequest route operationKind payload =
    { RequestId = Guid.NewGuid().ToString("N")
      Route = route
      OperationKind = operationKind
      Payload = payload
      Timeout = Some(TimeSpan.FromSeconds 30.0)
      UsePackageTargets = None }

let private createRouter () =
    let agentRegistry = InMemoryAgentRegistry() :> IAgentRegistry
    let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
    let sessionRegistry = InMemorySessionRegistry() :> ISessionRegistry
    let resultRegistry = InMemoryResultRegistry() :> IResultRegistry
    let backend = InProcBackend() :> IFsiExecutionBackend
    let selector = BackendSelector([ backend ])
    let router = ExecutionRouter(agentRegistry, hostRegistry, sessionRegistry, resultRegistry, selector)

    agentRegistry, hostRegistry, sessionRegistry, resultRegistry, router

[<Fact>]
let ``ExecutionRouter persists default-route results and updates session metadata`` () =
    task {
        let agentRegistry, _, sessionRegistry, resultRegistry, router = createRouter ()
        let route = router.ResolveRoute None

        let! record = router.RouteAndExecute(createRequest route AddSearchPath "/tmp")

        let results = resultRegistry.ListBySession(route)
        let session = sessionRegistry.TryGet(route.HostId, route.SessionId) |> Option.get

        Assert.True(record.Result.IsSuccess)
        Assert.Equal(1, results.Length)
        Assert.Equal(record.ResultId, results.Head.ResultId)
        Assert.Contains("/tmp", session.SearchPaths)
        Assert.Equal(SessionReady, session.Status)
        Assert.True(agentRegistry.TryGet(route.AgentId).IsSome)
    }

[<Fact>]
let ``ExecutionRouter preserves explicit route and stores multiple execution records`` () =
    task {
        let agentRegistry, hostRegistry, sessionRegistry, resultRegistry, router = createRouter ()
        let now = DateTime.UtcNow

        agentRegistry.Register(
            { AgentId = "agent-explicit"
              DisplayName = Some "Explicit Agent"
              CreatedAt = now
              LastSeenAt = now
              DefaultHostId = Some "host-explicit"
              Metadata = Map.empty }
        )
        |> ignore

        hostRegistry.Create(
            { HostId = "host-explicit"
              AgentId = "agent-explicit"
              HostKind = InProcHost
              BackendKind = InProc
              Status = Ready
              Address = None
              ProcId = None
              CreatedAt = now
              LastHealthCheckAt = Some now
              LastError = None }
        )
        |> ignore

        sessionRegistry.Create(
            { SessionId = "session-explicit"
              AgentId = "agent-explicit"
              HostId = "host-explicit"
              SessionName = "session-explicit"
              Status = SessionReady
              Refs = []
              Loads = []
              SearchPaths = []
              Variables = []
              LastCheckpointId = None
              RunningSinceUtc = Some now
              LastExecutionAt = None }
        )
        |> ignore

        let route =
            { AgentId = "agent-explicit"
              HostId = "host-explicit"
              SessionId = "session-explicit" }

        let! _ = router.RouteAndExecute(createRequest route ExecuteCode "let routedValue = 11")
        let! evalRecord = router.RouteAndExecute(createRequest route EvaluateExpression "routedValue")

        let results = resultRegistry.ListBySession(route)
        let session = sessionRegistry.TryGet(route.HostId, route.SessionId) |> Option.get

        Assert.True(evalRecord.Result.IsSuccess)
        Assert.Equal(Some "11", evalRecord.Result.Value)
        Assert.Equal(2, results.Length)
        Assert.True(results |> List.exists (fun record -> record.ResultId = evalRecord.ResultId))
        Assert.True(session.LastExecutionAt.IsSome)
    }
