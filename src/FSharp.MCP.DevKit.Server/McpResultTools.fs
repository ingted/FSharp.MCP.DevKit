namespace FSharp.MCP.DevKit.Server

open System
open System.ComponentModel
open System.Text.Json
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
        | _ -> BuiltIn

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
        | other -> invalidOp $"Unsupported result query kind '{other}'."

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
        |> JsonSerializer.Serialize

    [<McpServerTool(Name = "list_fsi_results"); Description("List execution results for an agent, optionally narrowed to a specific host/session.")>]
    static member ListFsiResults
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Optional host id filter. Requires sessionId when used.")>] ?hostId: string,
            [<Description("Optional session id filter. Requires hostId when used.")>] ?sessionId: string
        ) : string =
        match hostId, sessionId with
        | Some resolvedHostId, Some resolvedSessionId ->
            let route =
                { AgentId = agentId
                  HostId = resolvedHostId
                  SessionId = resolvedSessionId }

            fsiService.ListSessionResults(route)
            |> JsonSerializer.Serialize
        | None, None ->
            fsiService.ListAgentResults(agentId)
            |> JsonSerializer.Serialize
        | _ -> invalidOp "hostId and sessionId must be provided together."

    [<McpServerTool(Name = "query_fsi_results"); Description("Run a built-in result query over one or two result id sets. Best flow for agents: 1. Collect result ids from execution or list_fsi_results. 2. Call query_fsi_results. 3. If materialization is enabled, reuse the returned produced result id in later queries.")>]
    static member QueryFsiResults
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Query kind: filter, map, exists, forall, zip, diff, groupBy.")>] kind: string,
            [<Description("Primary result ids, separated by newlines, commas, or semicolons.")>] primaryResultIds: string,
            [<Description("Optional secondary result ids, separated by newlines, commas, or semicolons.")>] ?secondaryResultIds: string,
            [<Description("Optional query text. Examples: isSuccess, value, hostId, backendKind:Net10Remote, valueContains:foo.")>] ?queryText: string,
            [<Description("Optional language. builtIn (default) or fsharpCode.")>] ?language: string,
            [<Description("Optional materialization mode. none (default) or syntheticResult.")>] ?materialization: string
        ) : string =
        let request =
            { QueryId = Guid.NewGuid().ToString("N")
              AgentId = agentId
              PrimaryResultIds = splitIds primaryResultIds
              SecondaryResultIds = secondaryResultIds |> Option.map splitIds |> Option.defaultValue []
              Language = parseLanguage language
              Kind = parseKind kind
              QueryText = defaultArg queryText ""
              Materialization = parseMaterialization materialization }

        fsiService.QueryResults(request) |> JsonSerializer.Serialize

    [<McpServerTool(Name = "compare_fsi_results"); Description("Compare two ordered result id sets and return a diff-style response.")>]
    static member CompareFsiResults
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Primary result ids, separated by newlines, commas, or semicolons.")>] primaryResultIds: string,
            [<Description("Secondary result ids, separated by newlines, commas, or semicolons.")>] secondaryResultIds: string,
            [<Description("Optional compare field. Defaults to value.")>] ?queryText: string,
            [<Description("Optional materialization mode. none (default) or syntheticResult.")>] ?materialization: string
        ) : string =
        let request =
            { QueryId = Guid.NewGuid().ToString("N")
              AgentId = agentId
              PrimaryResultIds = splitIds primaryResultIds
              SecondaryResultIds = splitIds secondaryResultIds
              Language = BuiltIn
              Kind = Diff
              QueryText = defaultArg queryText "value"
              Materialization = parseMaterialization materialization }

        fsiService.QueryResults(request) |> JsonSerializer.Serialize
