namespace FSharp.MCP.DevKit.Server.ControlPlane

open FSharp.MCP.DevKit.Core

type EnsureRouteResponse =
    { Agent: AgentRecord
      Host: HostRecord
      Session: SessionRecord
      Route: ExecutionRoute
      CreatedAgent: bool
      CreatedHost: bool
      CreatedSession: bool
      Notes: string list
      RecommendedNextTools: string list }
