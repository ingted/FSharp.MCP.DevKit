namespace FSharp.MCP.DevKit.Server

open System
open System.ComponentModel
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Server.ResultQuery
open ModelContextProtocol.Server

[<AutoOpen>]
module private McpResultToolParsing =

    let splitIds (value: string) =
        if String.IsNullOrWhiteSpace value then
            []
        else
            value.Split([| '\n'; '\r'; ','; ';' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun item -> item.Trim())
            |> Array.filter (fun item -> not (String.IsNullOrWhiteSpace item))
            |> Array.toList

    let parseLanguage (value: string option) =
        match value |> Option.map (fun item -> item.Trim().ToLowerInvariant()) with
        | Some "fsharp"
        | Some "fsharpcode" -> FSharpCode
        | Some "builtin"
        | Some ""
        | None -> BuiltIn
        | Some other -> invalidOp $"Unknown language '{other}'. Valid values: builtIn, fsharpCode."

    let parseKind (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "filter" -> Filter
        | "map" -> Map
        | "exists" -> Exists
        | "forall" -> ForAll
        | "zip" -> Zip
        | "diff"
        | "compare" -> Diff
        | "groupby" -> GroupBy
        | other -> invalidOp $"Unsupported result query kind '{other}'. Valid values: filter, map, exists, forall, zip, diff, groupby."

    let parseMaterialization (value: string option) =
        match value |> Option.map (fun item -> item.Trim().ToLowerInvariant()) with
        | Some "synthetic"
        | Some "syntheticresult"
        | Some "result" -> SyntheticResult
        | _ -> NoMaterialization

[<McpServerToolType>]
type McpResultTools =

    [<McpServerTool(Name = "get_fsi_result"); Description("Get a single execution result by resultId, scoped to an agent.")>]
    static member GetFsiResult
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target result id.")>] resultId: string
        ) : string =
        fsiService.TryGetResultForAgent(agentId, resultId)
        |> FSharpJson.serialize

    [<McpServerTool(Name = "list_fsi_results"); Description("List execution results for an agent, optionally narrowed to a specific host/session.")>]
    static member ListFsiResults
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Optional host id filter. Use an empty string to omit.")>] hostId: string,
            [<Description("Optional session id filter. Use an empty string to omit.")>] sessionId: string
        ) : string =
        let hostIdOpt = if String.IsNullOrWhiteSpace hostId then None else Some hostId
        let sessionIdOpt = if String.IsNullOrWhiteSpace sessionId then None else Some sessionId

        match hostIdOpt, sessionIdOpt with
        | Some resolvedHostId, Some resolvedSessionId ->
            let route =
                { AgentId = agentId
                  HostId = resolvedHostId
                  SessionId = resolvedSessionId }

            fsiService.ListSessionResults(route)
            |> FSharpJson.serialize
        | None, None ->
            fsiService.ListAgentResults(agentId)
            |> FSharpJson.serialize
        | _ -> invalidOp "Both hostId and sessionId must be provided together, or both must be omitted for an unfiltered query."

    [<McpServerTool(Name = "query_fsi_results"); Description("Run a built-in result query over one or two result id sets. Best flow for agents: 1. Collect result ids from execution or list_fsi_results. 2. Call query_fsi_results. 3. If materialization is enabled, reuse the returned produced result id in later queries.")>]
    static member QueryFsiResults
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Query kind: filter, map, exists, forall, zip, diff, groupBy.")>] kind: string,
            [<Description("Primary result ids, separated by newlines, commas, or semicolons.")>] primaryResultIds: string,
            [<Description("Optional secondary result ids, separated by newlines, commas, or semicolons. Use an empty string to omit.")>] secondaryResultIds: string,
            [<Description("Optional query text. Examples: isSuccess, value, hostId, backendKind:Net10Remote, valueContains:foo. Use an empty string to omit.")>] queryText: string,
            [<Description("Optional language. builtIn (default) or fsharpCode. Use an empty string for builtIn.")>] language: string,
            [<Description("Optional materialization mode. none (default) or syntheticResult. Use an empty string for none.")>] materialization: string
        ) : string =
        let request =
            { QueryId = Guid.NewGuid().ToString("N")
              AgentId = agentId
              PrimaryResultIds = splitIds primaryResultIds
              SecondaryResultIds = splitIds secondaryResultIds
              Language = parseLanguage (if String.IsNullOrWhiteSpace language then None else Some language)
              Kind = parseKind kind
              QueryText = if String.IsNullOrWhiteSpace queryText then "" else queryText
              Materialization = parseMaterialization (if String.IsNullOrWhiteSpace materialization then None else Some materialization) }

        fsiService.QueryResults(request) |> FSharpJson.serialize

    [<McpServerTool(Name = "compare_fsi_results"); Description("Compare two ordered result id sets and return a diff-style response.")>]
    static member CompareFsiResults
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Primary result ids, separated by newlines, commas, or semicolons.")>] primaryResultIds: string,
            [<Description("Secondary result ids, separated by newlines, commas, or semicolons.")>] secondaryResultIds: string,
            [<Description("Optional compare field. Defaults to value. Use an empty string for the default.")>] queryText: string,
            [<Description("Optional materialization mode. none (default) or syntheticResult. Use an empty string for none.")>] materialization: string
        ) : string =
        let request =
            { QueryId = Guid.NewGuid().ToString("N")
              AgentId = agentId
              PrimaryResultIds = splitIds primaryResultIds
              SecondaryResultIds = splitIds secondaryResultIds
              Language = BuiltIn
              Kind = Diff
              QueryText = if String.IsNullOrWhiteSpace queryText then "value" else queryText
              Materialization = parseMaterialization (if String.IsNullOrWhiteSpace materialization then None else Some materialization) }

        fsiService.QueryResults(request) |> FSharpJson.serialize

    [<McpServerTool(Name = "subscribe_session_output"); Description("Subscribe to live session output for a specific route.")>]
    static member SubscribeSessionOutput
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("Subscriber id.")>] subscriberId: string,
            [<Description("Optional starting sequence number. Use 0 to start from the beginning of the live cache.")>] fromSequenceNo: int64,
            [<Description("Whether the subscriber expects replay from the requested sequence number.")>] includeHistory: bool
        ) : string =
        fsiService.SubscribeSessionOutput(
            subscriberId,
            fromSequenceNo = fromSequenceNo,
            includeHistory = includeHistory,
            requestedRoute = { AgentId = agentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize

    [<McpServerTool(Name = "list_session_output_subscribers"); Description("List live output subscribers for a specific route.")>]
    static member ListSessionOutputSubscribers
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string
        ) : string =
        fsiService.ListSessionOutputSubscribers(requestedRoute = { AgentId = agentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize

    [<McpServerTool(Name = "get_session_output_events"); Description("Read live session output events for a specific route.")>]
    static member GetSessionOutputEvents
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("Optional starting sequence number. Use 0 to read all cached live events.")>] afterSequenceNo: int64,
            [<Description("Optional maximum number of events to return. Use 0 for default.")>] limit: int
        ) : string =
        let limitOpt = if limit <= 0 then None else Some limit

        fsiService.ListSessionOutput(
            afterSequenceNo = afterSequenceNo,
            ?limit = limitOpt,
            requestedRoute = { AgentId = agentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize

    [<McpServerTool(Name = "unsubscribe_session_output"); Description("Remove a live output subscriber from a specific route.")>]
    static member UnsubscribeSessionOutput
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("Subscriber id.")>] subscriberId: string
        ) : string =
        fsiService.UnsubscribeSessionOutput(subscriberId, requestedRoute = { AgentId = agentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize

    [<McpServerTool(Name = "seal_session_output"); Description("Seal the current session output into archive immediately, without requiring a reset or host restart. Useful when a human or agent wants an explicit archive boundary before a lifecycle transition.")>]
    static member SealSessionOutput
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string
        ) : string =
        fsiService.SealSessionOutputArchive(requestedRoute = { AgentId = agentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize

    [<McpServerTool(Name = "get_session_output_archive"); Description("Read archive metadata for a specific route, if the session output has already been sealed into archive.")>]
    static member GetSessionOutputArchive
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string
        ) : string =
        fsiService.TryGetSessionOutputArchive(requestedRoute = { AgentId = agentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize

    [<McpServerTool(Name = "get_session_output_seal_pending"); Description("Read the seal-pending status for a specific route, if archive sealing previously failed.")>]
    static member GetSessionOutputSealPending
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string
        ) : string =
        fsiService.TryGetSessionOutputSealPending(requestedRoute = { AgentId = agentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize

    [<McpServerTool(Name = "recover_session_output_seal_pending"); Description("Attempt to recover a previously seal-pending session output archive.")>]
    static member RecoverSessionOutputSealPending
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string
        ) : string =
        fsiService.RecoverSessionOutputSealPending(requestedRoute = { AgentId = agentId; HostId = hostId; SessionId = sessionId })
        |> FSharpJson.serialize
