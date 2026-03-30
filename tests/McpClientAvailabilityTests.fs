namespace FSharp.MCP.DevKit.Tests

open System.Linq
open System.Threading.Tasks
open FSharp.MCP.DevKit.Tests.McpClientTestHelpers
open Xunit

[<Collection("mcp-client-e2e")>]
type McpClientAvailabilityTests() =

    [<Fact>]
    member _.``MCP client can ping server and discover core tools and resources``() =
        withClient (fun client ->
            task {
                let! _ = client.PingAsync()
                let! toolNames = client.ListToolNamesAsync()
                let! resourceUris = client.ListResourceUrisAsync()
                let! templateUris = client.ListResourceTemplateUrisAsync()

                assertContains "execute_f_sharp_code" toolNames
                assertContains "execute_f_sharp_code_async" toolNames
                assertContains "get_async_status" toolNames
                assertContains "register_fsi_agent" toolNames
                assertContains "ensure_fsi_route" toolNames
                assertContains "query_fsi_results" toolNames
                assertContains "worldtime" resourceUris
                assertContains "fsi/async/{asyncId}" templateUris
                assertContains "fsi/hosts/{hostId}/sessions/{sessionId}" templateUris
                assertContains "fsi/results/{resultId}" templateUris
            })

    [<Fact>]
    member _.``MCP client can read direct and templated resources``() =
        withClient (fun client ->
            task {
                let! worldTime = client.ReadResourceTextAsync("worldtime")
                let! taipei = client.ReadResourceTextAsync("time/Asia-Taipei")

                Assert.Contains("\"tz\":\"Asia/Taipei\"", worldTime)
                Assert.Contains("\"tz\":\"Asia-Taipei\"", taipei)
            })
