module Program

open System
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.ResultQuery

let private printJson value =
    FSharpJson.serialize value |> printfn "%s"

let private runWithClient (action: McpClientSession -> Task<unit>) =
    task {
        let! client = McpClientHarness.createStdioClientAsync()

        try
            do! action client
        finally
            (client :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

let private runDiscover (client: McpClientSession) =
    task {
        let! tools = client.ListToolNamesAsync()
        let! resources = client.ListResourceUrisAsync()
        let! templates = client.ListResourceTemplateUrisAsync()

        printJson
            {| Scenario = "discover"
               ToolCount = tools.Length
               SampleTools = tools |> List.truncate 12
               Resources = resources
               ResourceTemplates = templates |> List.truncate 12 |}
    }

let private runLegacyRoundTrip (client: McpClientSession) =
    task {
        let! _ =
            client.CallToolTextAsync(
                "execute_f_sharp_code",
                McpClientHarness.arguments [ "code", box "let demoValue = 41"; "timeoutSeconds", box 30 ]
            )

        let! value =
            client.CallToolTextAsync(
                "evaluate_f_sharp_expression",
                McpClientHarness.arguments [ "expression", box "demoValue + 1"; "timeoutSeconds", box 30 ]
            )

        let! state =
            client.CallToolTextAsync(
                "get_fsi_state",
                McpClientHarness.arguments [ "timeoutSeconds", box 30 ]
            )

        printJson
            {| Scenario = "legacy-roundtrip"
               Value = value.Trim()
               StatePreview = state.Split('\n') |> Array.truncate 8 |}
    }

let private runEnsureDefaultRoute (client: McpClientSession) =
    task {
        let! ensured =
            client.EnsureRouteAsync(
                DefaultRouting.DefaultAgentId,
                displayName = "Default Agent",
                hostId = DefaultRouting.DefaultHostId,
                sessionId = DefaultRouting.DefaultSessionId
            )

        printJson
            {| Scenario = "ensure-default-route"
               Route = ensured.Route
               CreatedAgent = ensured.CreatedAgent
               CreatedHost = ensured.CreatedHost
               CreatedSession = ensured.CreatedSession
               Notes = ensured.Notes |}
    }

let private runAsyncRoundTrip (client: McpClientSession) =
    task {
        let! asyncId =
            client.CallToolTextAsync(
                "execute_f_sharp_code_async",
                McpClientHarness.arguments [ "code", box "let asyncDemo = 123"; "timeoutSeconds", box 30 ]
            )

        let asyncId = asyncId.Trim()
        let! status = client.WaitForAsyncStatusAsync(asyncId)
        let! result = client.ReadResourceJsonAsync<FsiExecutionRecord option>($"fsi/results/{status.ResultId.Value}")

        printJson
            {| Scenario = "async-roundtrip"
               AsyncId = asyncId
               Status = status
               Result = result |}
    }

let private runResultAggregation (client: McpClientSession) =
    task {
        let! _ =
            client.CallToolTextAsync(
                "execute_f_sharp_code",
                McpClientHarness.arguments [ "code", box "let aggValue = 10"; "timeoutSeconds", box 30 ]
            )

        let! _ =
            client.CallToolTextAsync(
                "execute_f_sharp_code",
                McpClientHarness.arguments [ "code", box "let aggValue = 20"; "timeoutSeconds", box 30 ]
            )

        let! results = client.ReadResourceJsonAsync<FsiExecutionRecord list>("fsi/agents/default-agent/results")

        let latestTwo =
            results
            |> List.rev
            |> List.take 2
            |> List.rev
            |> List.map (fun record -> record.ResultId)
            |> String.concat ","

        let! builtInQuery =
            client.CallToolJsonAsync<ResultQueryResponse>(
                "query_fsi_results",
                McpClientHarness.arguments [ "agentId", box DefaultRouting.DefaultAgentId
                                             "kind", box "exists"
                                             "primaryResultIds", box latestTwo
                                             "secondaryResultIds", box ""
                                             "queryText", box "valueContains:20"
                                             "language", box ""
                                             "materialization", box "" ]
            )

        let! fsharpQuery =
            client.CallToolJsonAsync<ResultQueryResponse>(
                "query_fsi_results",
                McpClientHarness.arguments [ "agentId", box DefaultRouting.DefaultAgentId
                                             "kind", box "map"
                                             "secondaryResultIds", box ""
                                             "language", box "fsharpCode"
                                             "primaryResultIds", box latestTwo
                                             "queryText", box "records1 |> Seq.map (fun record -> record.Result.Value |> Option.defaultValue \"\") |> Seq.toList"
                                             "materialization", box "" ]
            )

        printJson
            {| Scenario = "result-aggregation"
               LatestResultIds = latestTwo.Split(',') |> Array.toList
               BuiltIn = builtInQuery
               FSharpCode = fsharpQuery |}
    }

let private usage () =
    printfn "Usage: dotnet examples/FSharp.MCP.DevKit.DemoClient/bin/Debug/net10.0/FSharp.MCP.DevKit.DemoClient.dll <scenario>"
    printfn ""
    printfn "Scenarios:"
    printfn "  discover"
    printfn "  legacy-roundtrip"
    printfn "  ensure-default-route"
    printfn "  async-roundtrip"
    printfn "  result-aggregation"

let scenario = Environment.GetCommandLineArgs() |> Array.skip 1 |> Array.tryHead |> Option.defaultValue "discover"

let work =
    match scenario.Trim().ToLowerInvariant() with
    | "discover" -> runWithClient runDiscover
    | "legacy-roundtrip" -> runWithClient runLegacyRoundTrip
    | "ensure-default-route" -> runWithClient runEnsureDefaultRoute
    | "async-roundtrip" -> runWithClient runAsyncRoundTrip
    | "result-aggregation" -> runWithClient runResultAggregation
    | "help"
    | "--help"
    | "-h" ->
        task {
            usage ()
            return ()
        }
    | other ->
        usage ()
        raise (ArgumentException($"Unknown scenario '{other}'."))

work.GetAwaiter().GetResult()
