namespace FSharp.MCP.DevKit.Tests

open System
open System.Text
open System.Threading.Tasks
open FSharp.MCP.DevKit.Tests.McpClientTestHelpers
open Xunit

[<Collection("mcp-client-e2e")>]
type McpClientE2ETests() =

    [<Fact>]
    member _.``MCP client E2E runner executes all smoke scenarios without failures``() =
        task {
            let failures = ResizeArray<string>()

            for name, runScenario in McpClientSmokeScenarioCatalog.all do
                try
                    do! withClient runScenario
                with ex ->
                    failures.Add($"{name}: {ex.Message}")

            if failures.Count > 0 then
                let details = String.Join("\n", failures)
                Assert.True(false, $"One or more MCP client smoke scenarios failed:\n{details}")
        }
