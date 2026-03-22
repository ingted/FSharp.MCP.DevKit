namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open FSharp.MCP.DevKit.Core

module DefaultRouting =
    [<Literal>]
    let DefaultAgentId = "default-agent"

    [<Literal>]
    let DefaultHostId = "default-host"

    [<Literal>]
    let DefaultSessionId = "default-session"

    let private ensureDefaultAgent (agentRegistry: IAgentRegistry) =
        let now = DateTime.UtcNow

        match agentRegistry.TryGet DefaultAgentId with
        | Some existing ->
            let updated =
                { existing with
                    LastSeenAt = now
                    DefaultHostId = Some DefaultHostId }

            agentRegistry.Register updated |> ignore
            updated
        | None ->
            { AgentId = DefaultAgentId
              DisplayName = Some "Default Agent"
              CreatedAt = now
              LastSeenAt = now
              DefaultHostId = Some DefaultHostId
              Metadata = Map.empty }
            |> agentRegistry.Register

    let private ensureDefaultHost (hostRegistry: IHostRegistry) =
        let now = DateTime.UtcNow

        match hostRegistry.TryGet DefaultHostId with
        | Some existing -> existing
        | None ->
            { HostId = DefaultHostId
              AgentId = DefaultAgentId
              HostKind = InProcHost
              BackendKind = InProc
              Status = Ready
              Address = None
              ProcId = None
              CreatedAt = now
              LastHealthCheckAt = Some now
              LastError = None }
            |> hostRegistry.Create

    let private ensureDefaultSession (sessionRegistry: ISessionRegistry) =
        match sessionRegistry.TryGet(DefaultHostId, DefaultSessionId) with
        | Some existing -> existing
        | None ->
            { SessionId = DefaultSessionId
              AgentId = DefaultAgentId
              HostId = DefaultHostId
              SessionName = "Default Session"
              Status = SessionReady
              Refs = []
              Loads = []
              SearchPaths = []
              Variables = []
              LastCheckpointId = None
              RunningSinceUtc = Some DateTime.UtcNow
              LastExecutionAt = None }
            |> sessionRegistry.Create

    let private validateRoute
        (agentRegistry: IAgentRegistry)
        (hostRegistry: IHostRegistry)
        (sessionRegistry: ISessionRegistry)
        (route: ExecutionRoute)
        =
        let agent =
            agentRegistry.TryGet route.AgentId
            |> Option.defaultWith (fun () -> invalidOp $"Agent '{route.AgentId}' was not found.")

        let host =
            hostRegistry.TryGet route.HostId
            |> Option.defaultWith (fun () -> invalidOp $"Host '{route.HostId}' was not found.")

        if host.AgentId <> agent.AgentId then
            invalidOp $"Host '{route.HostId}' does not belong to agent '{route.AgentId}'."

        let session =
            sessionRegistry.TryGet(route.HostId, route.SessionId)
            |> Option.defaultWith (fun () ->
                invalidOp $"Session '{route.SessionId}' was not found under host '{route.HostId}'.")

        if session.AgentId <> route.AgentId then
            invalidOp $"Session '{route.SessionId}' does not belong to agent '{route.AgentId}'."

        route

    let resolve
        (agentRegistry: IAgentRegistry)
        (hostRegistry: IHostRegistry)
        (sessionRegistry: ISessionRegistry)
        (requestedRoute: ExecutionRoute option)
        =
        match requestedRoute with
        | Some route -> validateRoute agentRegistry hostRegistry sessionRegistry route
        | None ->
            ensureDefaultAgent agentRegistry |> ignore
            ensureDefaultHost hostRegistry |> ignore
            ensureDefaultSession sessionRegistry |> ignore

            { AgentId = DefaultAgentId
              HostId = DefaultHostId
              SessionId = DefaultSessionId }
