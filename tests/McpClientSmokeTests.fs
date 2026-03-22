namespace FSharp.MCP.DevKit.Tests

open System
open System.Collections.Generic
open System.Linq
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Tests.McpClientTestHelpers
open Xunit

module private McpClientSmokeScenarioCatalog =

    let private callText (client: McpClientSession) toolName pairs =
        client.CallToolTextAsync(toolName, McpClientHarness.arguments pairs)

    let private callJson<'T> (client: McpClientSession) toolName pairs =
        client.CallToolJsonAsync<'T>(toolName, McpClientHarness.arguments pairs)

    let private readJson<'T> (client: McpClientSession) uri = client.ReadResourceJsonAsync<'T>(uri)

    let bindingPersistsScenario (client: McpClientSession) =
        task {
            let! _ = callText client "execute_f_sharp_code" [ "code", box "let x = 1"; "timeoutSeconds", box 30 ]
            let! value = callText client "evaluate_f_sharp_expression" [ "expression", box "x"; "timeoutSeconds", box 30 ]
            Assert.Equal("1", value.Trim())
        }

    let implicitItScenario (client: McpClientSession) =
        task {
            let! _ = callText client "execute_f_sharp_code" [ "code", box "456"; "timeoutSeconds", box 30 ]
            let! value = callText client "evaluate_f_sharp_expression" [ "expression", box "it"; "timeoutSeconds", box 30 ]
            Assert.Equal("456", value.Trim())
        }

    let shadowingScenario (client: McpClientSession) =
        task {
            let! _ = callText client "execute_f_sharp_code" [ "code", box "let x = 1"; "timeoutSeconds", box 30 ]
            let! _ = callText client "execute_f_sharp_code" [ "code", box "let z = 100"; "timeoutSeconds", box 30 ]
            let! _ = callText client "execute_f_sharp_code" [ "code", box "let x = (1, 2)"; "timeoutSeconds", box 30 ]
            let! _ = callText client "execute_f_sharp_code" [ "code", box "let w = obj ()"; "timeoutSeconds", box 30 ]
            let! value = callText client "evaluate_f_sharp_expression" [ "expression", box "x"; "timeoutSeconds", box 30 ]
            Assert.Contains("(1, 2)", value)
        }

    let resetScenario (client: McpClientSession) =
        task {
            let! _ = callText client "execute_f_sharp_code" [ "code", box "let resetValue = 12"; "timeoutSeconds", box 30 ]
            let! beforeReset = callText client "evaluate_f_sharp_expression" [ "expression", box "resetValue"; "timeoutSeconds", box 30 ]
            let! _ = callText client "reset_fsi_session" [ "timeoutSeconds", box 30 ]
            let! afterReset = callText client "evaluate_f_sharp_expression" [ "expression", box "resetValue"; "timeoutSeconds", box 30 ]

            Assert.Equal("12", beforeReset.Trim())
            Assert.Contains("resetValue", afterReset)
        }

    let asyncStatusScenario (client: McpClientSession) =
        task {
            let! asyncId =
                callText client "execute_f_sharp_code_async" [ "code", box "let asyncSmoke = 9"; "timeoutSeconds", box 30 ]

            let! status = waitForAsyncStatus client (asyncId.Trim())
            let! result =
                callJson<FsiExecutionRecord option>
                    client
                    "get_fsi_result"
                    [ "agentId", box "default-agent"
                      "resultId", box status.ResultId.Value ]

            Assert.True(status.IsCompleted)
            Assert.True(status.Result.IsSome)
            Assert.True(status.Result.Value.IsSuccess)
            Assert.True(result.IsSome)
            Assert.Equal(status.ResultId.Value, result.Value.ResultId)
            Assert.Equal("default-host", result.Value.HostId)
            Assert.Equal("default-session", result.Value.SessionId)
        }

    let multiSessionIsolationScenario (client: McpClientSession) =
        task {
            let! _ = bootstrapDefaultRoute client
            let! _ = createDefaultSession client "session-a"
            let! _ = createDefaultSession client "session-b"

            let routedArgs sessionId code =
                [ "agentId", box "default-agent"
                  "hostId", box "default-host"
                  "sessionId", box sessionId
                  "code", box code
                  "timeoutSeconds", box 30 ]

            let evalArgs sessionId expression =
                [ "agentId", box "default-agent"
                  "hostId", box "default-host"
                  "sessionId", box sessionId
                  "expression", box expression
                  "timeoutSeconds", box 30 ]

            let! _ = callText client "execute_f_sharp_code_routed" (routedArgs "session-a" "let sessionValue = 11")
            let! _ = callText client "execute_f_sharp_code_routed" (routedArgs "session-b" "let sessionValue = 22")
            let! valueA = callText client "evaluate_f_sharp_expression_routed" (evalArgs "session-a" "sessionValue")
            let! valueB = callText client "evaluate_f_sharp_expression_routed" (evalArgs "session-b" "sessionValue")
            let! stateA = readJson<SessionRecord option> client "fsi/hosts/default-host/sessions/session-a"
            let! stateB = readJson<SessionRecord option> client "fsi/hosts/default-host/sessions/session-b"

            Assert.Equal("11", valueA.Trim())
            Assert.Equal("22", valueB.Trim())
            Assert.True(stateA.IsSome)
            Assert.True(stateB.IsSome)
            Assert.Equal("session-a", stateA.Value.SessionId)
            Assert.Equal("session-b", stateB.Value.SessionId)
        }

    let resultQueryScenario (client: McpClientSession) =
        task {
            let! _ = callText client "execute_f_sharp_code" [ "code", box "let queryValue = 5"; "timeoutSeconds", box 30 ]
            let! _ = callText client "execute_f_sharp_code" [ "code", box "let queryValue = 9"; "timeoutSeconds", box 30 ]
            let! results = readJson<FsiExecutionRecord list> client "fsi/hosts/default-host/sessions/default-session/results"

            let latestTwo =
                results
                |> List.rev
                |> List.take 2
                |> List.rev
                |> List.map (fun record -> record.ResultId)
                |> String.concat ","

            let! existsResponse =
                callJson<FSharp.MCP.DevKit.Server.ResultQuery.ResultQueryResponse>
                    client
                    "query_fsi_results"
                    [ "agentId", box "default-agent"
                      "kind", box "exists"
                      "primaryResultIds", box latestTwo
                      "queryText", box "valueContains:9" ]

            Assert.True(existsResponse.IsSuccess)
            Assert.Equal("true", existsResponse.Output)
        }

    let fsharpResultQueryScenario (client: McpClientSession) =
        task {
            let! _ = callText client "execute_f_sharp_code" [ "code", box "let fsharpQueryValue = 15"; "timeoutSeconds", box 30 ]
            let! _ = callText client "execute_f_sharp_code" [ "code", box "let fsharpQueryValue = 19"; "timeoutSeconds", box 30 ]
            let! results = readJson<FsiExecutionRecord list> client "fsi/hosts/default-host/sessions/default-session/results"

            let latestTwo =
                results
                |> List.rev
                |> List.take 2
                |> List.rev
                |> List.map (fun record -> record.ResultId)
                |> String.concat ","

            let! response =
                callJson<FSharp.MCP.DevKit.Server.ResultQuery.ResultQueryResponse>
                    client
                    "query_fsi_results"
                    [ "agentId", box "default-agent"
                      "kind", box "map"
                      "language", box "fsharpCode"
                      "primaryResultIds", box latestTwo
                      "queryText", box "records1 |> Seq.map (fun record -> record.Result.Value |> Option.defaultValue \"\") |> Seq.toList" ]

            Assert.True(response.IsSuccess)
            Assert.Equal("[\"15\",\"19\"]", response.MaterializedJson.Value)
        }

    let all : (string * (McpClientSession -> Task<unit>)) array =
        [| "binding-persists", bindingPersistsScenario
           "implicit-it", implicitItScenario
           "shadowing", shadowingScenario
           "reset", resetScenario
           "async-status", asyncStatusScenario
           "multi-session-isolation", multiSessionIsolationScenario
           "result-query", resultQueryScenario
           "fsharp-result-query", fsharpResultQueryScenario |]

[<Collection("mcp-client-e2e")>]
type McpClientSmokeTests() =

    [<Fact>]
    member _.``Client smoke follows FSI bound-value persistence pattern``() =
        withClient McpClientSmokeScenarioCatalog.bindingPersistsScenario

    [<Fact>]
    member _.``Client smoke follows implicit it pattern``() =
        withClient McpClientSmokeScenarioCatalog.implicitItScenario

    [<Fact>]
    member _.``Client smoke follows latest-shadowed-value pattern``() =
        withClient McpClientSmokeScenarioCatalog.shadowingScenario

    [<Fact>]
    member _.``Client smoke follows reset-clears-state pattern``() =
        withClient McpClientSmokeScenarioCatalog.resetScenario

    [<Fact>]
    member _.``Client smoke covers async status and result linkage``() =
        withClient McpClientSmokeScenarioCatalog.asyncStatusScenario

    [<Fact>]
    member _.``Client smoke covers multi-session isolation``() =
        withClient McpClientSmokeScenarioCatalog.multiSessionIsolationScenario

    [<Fact>]
    member _.``Client smoke covers built-in result query``() =
        withClient McpClientSmokeScenarioCatalog.resultQueryScenario

    [<Fact>]
    member _.``Client smoke covers fsharp-code result query``() =
        withClient McpClientSmokeScenarioCatalog.fsharpResultQueryScenario
