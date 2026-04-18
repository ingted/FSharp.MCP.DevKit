namespace FSharp.MCP.DevKit.Server

open System.ComponentModel
open System.Threading.Tasks
open ModelContextProtocol.Server
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Server.ControlPlane

[<McpServerResourceType>]
type ResultResources(fsiService: FsiMcpService) =

    let tryResolveRoute (hostId: string) (sessionId: string) : ExecutionRoute option =
        fsiService.TryResolveRouteByHostSession(hostId, sessionId)

    [<McpServerResource(Name = "fsiResult", Title = "FSI Result", MimeType = "application/json", UriTemplate = "fsi/results/{resultId}")>]
    [<Description("Read a single execution result by resultId.")>]
    member _.Result(resultId: string) =
        fsiService.TryGetResult(resultId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiExecutionFabricRecord", Title = "FSI Execution Fabric Record", MimeType = "application/json", UriTemplate = "fsi/results/{resultId}/execution-fabric")>]
    [<Description("Read one execution result projected into the shared FAkka execution fabric contract.")>]
    member _.ExecutionFabricRecord(resultId: string) =
        task {
            let! record = fsiService.TryGetExecutionFabricRecord(resultId)
            return record |> FSharpJson.serialize
        }

    [<McpServerResource(Name = "fsiAgentExecutionFabric", Title = "FSI Agent Execution Fabric", MimeType = "application/json", UriTemplate = "fsi/agents/{agentId}/execution-fabric")>]
    [<Description("List shared FAkka execution fabric records owned by an agent.")>]
    member _.AgentExecutionFabric(agentId: string) =
        task {
            let! records = fsiService.ListExecutionFabricRecords(agentId = agentId)
            return records |> FSharpJson.serialize
        }

    [<McpServerResource(Name = "fsiSessionExecutionFabric", Title = "FSI Session Execution Fabric", MimeType = "application/json", UriTemplate = "fsi/sessions/{sessionId}/execution-fabric")>]
    [<Description("List shared FAkka execution fabric records by session id, including archived sessions retained by the execution store.")>]
    member _.SessionExecutionFabric(sessionId: string) =
        task {
            let! records = fsiService.ListExecutionFabricRecords(sessionId = sessionId)
            return records |> FSharpJson.serialize
        }

    [<McpServerResource(Name = "fsiHostSessionExecutionFabric", Title = "FSI Host Session Execution Fabric", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/execution-fabric")>]
    [<Description("List shared FAkka execution fabric records by concrete host/session route.")>]
    member _.HostSessionExecutionFabric(hostId: string, sessionId: string) =
        task {
            let! records = fsiService.ListExecutionFabricRecords(hostId = hostId, sessionId = sessionId)
            return records |> FSharpJson.serialize
        }

    [<McpServerResource(Name = "fsiBrowserExecutionFabric", Title = "FSI Browser Execution Fabric", MimeType = "application/json", UriTemplate = "fsi/browsers/{browserId}/execution-fabric")>]
    [<Description("List shared FAkka execution fabric records by SharpBrowser/browser id.")>]
    member _.BrowserExecutionFabric(browserId: string) =
        task {
            let! records = fsiService.ListExecutionFabricRecords(browserId = browserId)
            return records |> FSharpJson.serialize
        }

    [<McpServerResource(Name = "fsiPrincipalExecutionFabric", Title = "FSI Principal Execution Fabric", MimeType = "application/json", UriTemplate = "fsi/principals/{principalId}/execution-fabric")>]
    [<Description("List shared FAkka execution fabric records by principal id.")>]
    member _.PrincipalExecutionFabric(principalId: string) =
        task {
            let! records = fsiService.ListExecutionFabricRecords(principalId = principalId)
            return records |> FSharpJson.serialize
        }

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

    [<McpServerResource(Name = "fsiSessionResultsBySessionId", Title = "FSI Session Results By Session Id", MimeType = "application/json", UriTemplate = "fsi/sessions/{sessionId}/results")>]
    [<Description("List execution results by session id only. This supports archived sessions that no longer have a live host/session route.")>]
    member _.SessionResultsBySessionId(sessionId: string) =
        fsiService.ListResultsBySessionId(sessionId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionOutput", Title = "FSI Session Output", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/output")>]
    [<Description("List live session output events under a specific host/session route.")>]
    member _.SessionOutput(hostId: string, sessionId: string) =
        match tryResolveRoute hostId sessionId with
        | Some route -> fsiService.ListSessionOutput(requestedRoute = route)
        | None -> []
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionOutputAfter", Title = "FSI Session Output After Sequence", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/output/{afterSequenceNo}")>]
    [<Description("List live session output events after the specified sequence number.")>]
    member _.SessionOutputAfter(hostId: string, sessionId: string, afterSequenceNo: int64) =
        match tryResolveRoute hostId sessionId with
        | Some route -> fsiService.ListSessionOutput(afterSequenceNo = afterSequenceNo, requestedRoute = route)
        | None -> []
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionOutputSubscribers", Title = "FSI Session Output Subscribers", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/output/subscribers")>]
    [<Description("List live output subscribers under a specific host/session route.")>]
    member _.SessionOutputSubscribers(hostId: string, sessionId: string) =
        match tryResolveRoute hostId sessionId with
        | Some route -> fsiService.ListSessionOutputSubscribers(requestedRoute = route)
        | None -> []
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionOutputArchive", Title = "FSI Session Output Archive", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/output/archive")>]
    [<Description("Read archive metadata for a specific host/session route, if the session output has already been sealed into archive.")>]
    member _.SessionOutputArchive(hostId: string, sessionId: string) =
        match tryResolveRoute hostId sessionId with
        | Some route -> fsiService.TryGetSessionOutputArchive(requestedRoute = route)
        | None -> None
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionOutputSealPending", Title = "FSI Session Output Seal Pending", MimeType = "application/json", UriTemplate = "fsi/hosts/{hostId}/sessions/{sessionId}/output/seal-pending")>]
    [<Description("Read the seal-pending status for a specific host/session route, if archive sealing previously failed.")>]
    member _.SessionOutputSealPending(hostId: string, sessionId: string) =
        match tryResolveRoute hostId sessionId with
        | Some route -> fsiService.TryGetSessionOutputSealPending(requestedRoute = route)
        | None -> None
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiSessionOutputArchives", Title = "FSI Session Output Archives", MimeType = "application/json", UriTemplate = "fsi/output/archives")>]
    [<Description("List archived session output metadata across the execution store.")>]
    member _.SessionOutputArchives() =
        fsiService.ListSessionOutputArchives()
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiArchivedSessionOutputArchive", Title = "Archived FSI Session Output Metadata", MimeType = "application/json", UriTemplate = "fsi/output/archives/{sessionId}")>]
    [<Description("Read archive metadata by archived session id, without requiring the session to still be registered as live.")>]
    member _.ArchivedSessionOutputArchive(sessionId: string) =
        fsiService.TryGetArchivedSessionOutputArchive(sessionId)
        |> FSharpJson.serialize

    [<McpServerResource(Name = "fsiArchivedSessionOutput", Title = "Archived FSI Session Output", MimeType = "application/json", UriTemplate = "fsi/output/archives/{sessionId}/output")>]
    [<Description("Read archived output events by session id, without requiring the session to still be registered as live.")>]
    member _.ArchivedSessionOutput(sessionId: string) =
        fsiService.ListArchivedSessionOutput(sessionId)
        |> FSharpJson.serialize
