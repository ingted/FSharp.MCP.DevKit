namespace FSharp.MCP.DevKit.Server

open System.ComponentModel
open ModelContextProtocol.Server
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Server.ControlPlane

[<McpServerResourceType>]
type ResultResources(fsiService: FsiMcpService) =

    [<McpServerResource(Name = "fsiResult", Title = "FSI Result", MimeType = "application/json", UriTemplate = "fsi/results/{resultId}")>]
    [<Description("Read a single execution result by resultId.")>]
    member _.Result(resultId: string) =
        fsiService.TryGetResult(resultId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiAgentResults", Title = "FSI Agent Results", MimeType = "application/json", UriTemplate = "fsi/agents/{agentId}/results")>]
    [<Description("List execution results owned by an agent.")>]
    member _.AgentResults(agentId: string) =
        fsiService.ListAgentResults(agentId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionResults", Title = "FSI Session Results", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/results")>]
    [<Description("List execution results under a specific host/session route.")>]
    member _.SessionResults(hostId: string, sessionId: string) =
        fsiService.ListHostSessionResults(hostId, sessionId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionOutput", Title = "FSI Session Output", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/output")>]
    [<Description("List live session output events under a specific host/session route.")>]
    member _.SessionOutput(hostId: string, sessionId: string) =
        fsiService.ListSessionOutput(requestedRoute = { AgentId = DefaultRouting.DefaultAgentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionOutputAfter", Title = "FSI Session Output After Sequence", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/output/{afterSequenceNo}")>]
    [<Description("List live session output events after the specified sequence number.")>]
    member _.SessionOutputAfter(hostId: string, sessionId: string, afterSequenceNo: int64) =
        fsiService.ListSessionOutput(afterSequenceNo = afterSequenceNo, requestedRoute = { AgentId = DefaultRouting.DefaultAgentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionOutputSubscribers", Title = "FSI Session Output Subscribers", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/output/subscribers")>]
    [<Description("List live output subscribers under a specific host/session route.")>]
    member _.SessionOutputSubscribers(hostId: string, sessionId: string) =
        fsiService.ListSessionOutputSubscribers(requestedRoute = { AgentId = DefaultRouting.DefaultAgentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionOutputSealPending", Title = "FSI Session Output Seal Pending", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/output/seal-pending")>]
    [<Description("Read the seal-pending status for a specific host/session route, if archive sealing previously failed.")>]
    member _.SessionOutputSealPending(hostId: string, sessionId: string) =
        fsiService.TryGetSessionOutputSealPending(requestedRoute = { AgentId = DefaultRouting.DefaultAgentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize
