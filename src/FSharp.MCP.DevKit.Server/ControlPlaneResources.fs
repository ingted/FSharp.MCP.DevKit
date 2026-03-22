namespace FSharp.MCP.DevKit.Server

open System.ComponentModel
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

    [<McpServerResource(Name = "fsiPathMappings", Title = "FSI Path Mappings", MimeType = "application/json", UriTemplate = "fsi/path-mappings")>]
    [<Description("List all registered path mappings.")>]
    member _.PathMappings() =
        fsiService.ListPathMappings()
        |> FSharpJson.serialize
