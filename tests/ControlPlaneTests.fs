module ControlPlaneTests

open System
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.ControlPlane

[<Fact>]
let ``DefaultRouting creates default agent host and session when route is omitted`` () =
    let agentRegistry = InMemoryAgentRegistry() :> IAgentRegistry
    let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
    let sessionRegistry = InMemorySessionRegistry() :> ISessionRegistry

    let route = DefaultRouting.resolve agentRegistry hostRegistry sessionRegistry None

    Assert.Equal(DefaultRouting.DefaultAgentId, route.AgentId)
    Assert.Equal(DefaultRouting.DefaultHostId, route.HostId)
    Assert.Equal(DefaultRouting.DefaultSessionId, route.SessionId)
    Assert.True(agentRegistry.TryGet(DefaultRouting.DefaultAgentId).IsSome)
    Assert.True(hostRegistry.TryGet(DefaultRouting.DefaultHostId).IsSome)
    Assert.True(sessionRegistry.TryGet(DefaultRouting.DefaultHostId, DefaultRouting.DefaultSessionId).IsSome)

[<Fact>]
let ``DefaultRouting returns explicit route when agent host and session are valid`` () =
    let agentRegistry = InMemoryAgentRegistry() :> IAgentRegistry
    let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
    let sessionRegistry = InMemorySessionRegistry() :> ISessionRegistry
    let now = DateTime.UtcNow

    agentRegistry.Register(
        { AgentId = "agent-a"
          DisplayName = None
          CreatedAt = now
          LastSeenAt = now
          DefaultHostId = Some "host-a"
          Metadata = Map.empty }
    )
    |> ignore

    hostRegistry.Create(
        { HostId = "host-a"
          AgentId = "agent-a"
          HostKind = InProcHost
          BackendKind = InProc
          Status = Ready
          Address = None
          ProcId = None
          CreatedAt = now
          LastHealthCheckAt = None
          LastError = None }
    )
    |> ignore

    sessionRegistry.Create(
        { SessionId = "session-a"
          AgentId = "agent-a"
          HostId = "host-a"
          SessionName = "session-a"
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
        DefaultRouting.resolve
            agentRegistry
            hostRegistry
            sessionRegistry
            (Some
                { AgentId = "agent-a"
                  HostId = "host-a"
                  SessionId = "session-a" })

    Assert.Equal("agent-a", route.AgentId)
    Assert.Equal("host-a", route.HostId)
    Assert.Equal("session-a", route.SessionId)

[<Fact>]
let ``DefaultRouting rejects route when host belongs to another agent`` () =
    let agentRegistry = InMemoryAgentRegistry() :> IAgentRegistry
    let hostRegistry = InMemoryHostRegistry() :> IHostRegistry
    let sessionRegistry = InMemorySessionRegistry() :> ISessionRegistry
    let now = DateTime.UtcNow

    agentRegistry.Register(
        { AgentId = "agent-a"
          DisplayName = None
          CreatedAt = now
          LastSeenAt = now
          DefaultHostId = None
          Metadata = Map.empty }
    )
    |> ignore

    hostRegistry.Create(
        { HostId = "host-b"
          AgentId = "agent-b"
          HostKind = InProcHost
          BackendKind = InProc
          Status = Ready
          Address = None
          ProcId = None
          CreatedAt = now
          LastHealthCheckAt = None
          LastError = None }
    )
    |> ignore

    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            DefaultRouting.resolve
                agentRegistry
                hostRegistry
                sessionRegistry
                (Some
                    { AgentId = "agent-a"
                      HostId = "host-b"
                      SessionId = "session-b" })
            |> ignore)

    Assert.Contains("does not belong to agent", ex.Message)
