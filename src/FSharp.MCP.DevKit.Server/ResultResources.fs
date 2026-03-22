namespace FSharp.MCP.DevKit.Server

open System.ComponentModel
open ModelContextProtocol.Server
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.McpFsiTools

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
