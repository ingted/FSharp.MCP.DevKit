namespace FSharp.MCP.DevKit.Tests

open System.Text.Json
open System.Threading.Tasks
open FSharp.MCP.DevKit.Tests.McpClientTestHelpers
open Xunit

[<Collection("mcp-client-e2e")>]
type DemoClientSmokeTests() =

    [<Fact>]
    member _.``Demo client scenarios run successfully``() =
        task {
            let scenarios =
                [| "discover"
                   "ensure-default-route"
                   "legacy-roundtrip"
                   "async-roundtrip"
                   "result-aggregation" |]

            for scenario in scenarios do
                let! exitCode, stdout, stderr = runDemoClientScenario scenario
                if exitCode <> 0 then
                    Assert.True(false, $"Scenario '{scenario}' failed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}")

                use document = JsonDocument.Parse(stdout)
                Assert.Equal(scenario, document.RootElement.GetProperty("scenario").GetString())
        }
