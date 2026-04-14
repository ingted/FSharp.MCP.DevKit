namespace FSharp.MCP.DevKit.Server

open System.ComponentModel
open System.Threading.Tasks
open ModelContextProtocol.Server
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.McpFsiTools

[<McpServerResourceType>]
type ControlPlaneResources(fsiService: FsiMcpService) =

    [<McpServerResource(Name = "fsiAgent", Title = "FSI Agent", MimeType = "application/json", UriTemplate = "fsi/agents/{agentId}")>]
    [<Description("Read a registered FSI agent by agentId.")>]
    member _.Agent(agentId: string) =
        fsiService.TryGetAgent(agentId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiHost", Title = "FSI Host", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}")>]
    [<Description("Read an FSI host by hostId.")>]
    member _.Host(hostId: string) =
        fsiService.TryGetHost(hostId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiHostSessions", Title = "FSI Host Sessions", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions")>]
    [<Description("List sessions under a host.")>]
    member _.HostSessions(hostId: string) =
        fsiService.ListHostSessions(hostId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiHostSession", Title = "FSI Host Session", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}")>]
    [<Description("Read a specific session under a host.")>]
    member _.HostSession(hostId: string, sessionId: string) =
        fsiService.TryGetSession(hostId, sessionId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiHostSessionState", Title = "FSI Host Session State", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/state")>]
    [<Description("Read the current backend-observed session state for a specific host/session route.")>]
    member _.HostSessionState(hostId: string, sessionId: string) : Task<string> =
        task {
            let! state = fsiService.TryGetSessionStateForHostSession(hostId, sessionId)
            return state |> FSharpJson.serialize
        }

    [<McpServerResource(Name = "fsiHostSessionLiveness", Title = "FSI Host Session Liveness", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/liveness")>]
    [<Description("Read a plain liveness projection for a specific host/session route, including unreachable state and observation time.")>]
    member _.HostSessionLiveness(hostId: string, sessionId: string) : Task<string> =
        task {
            let! state = fsiService.TryGetSessionLivenessForHostSession(hostId, sessionId)
            return state |> FSharpJson.serialize
        }

    [<McpServerResource(Name = "fsiHostSessionsLiveness", Title = "FSI Host Sessions Liveness", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions-liveness")>]
    [<Description("List liveness projections for all sessions under a specific host.")>]
    member _.HostSessionsLiveness(hostId: string) : Task<string> =
        task {
            let! states = fsiService.ListHostSessionLiveness(hostId)
            return states |> FSharpJson.serialize
        }

    [<McpServerResource(Name = "fsiInventoryEvents", Title = "FSI Inventory Events", MimeType = "application/json", UriTemplate = "fsi/inventory-events")>]
    [<Description("List inventory events for hosts and sessions.")>]
    member _.InventoryEvents() =
        fsiService.ListInventoryEvents()
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiInventoryEventsAfter", Title = "FSI Inventory Events After Sequence", MimeType = "application/json", UriTemplate = "fsi/inventory-events/{afterSequenceId}")>]
    [<Description("List inventory events after the specified sequence id.")>]
    member _.InventoryEventsAfter(afterSequenceId: int64) =
        fsiService.ListInventoryEvents(afterSequenceId = afterSequenceId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiPathMappings", Title = "FSI Path Mappings", MimeType = "application/json", UriTemplate = "fsi/path-mappings")>]
    [<Description("List all registered path mappings.")>]
    member _.PathMappings() =
        fsiService.ListPathMappings()
        |> FSharpJson.serialize
