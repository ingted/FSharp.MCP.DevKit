module McpResultToolsTests

open System
open System.Text.Json
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Server.ResultQuery

[<Fact>]
let ``McpResultTools get list query compare and resources work`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! _ = service.ExecuteOperation(ExecuteCode, "let resultQueryValue = 10", timeout = TimeSpan.FromSeconds 30.0)
        let! first = service.ExecuteOperation(EvaluateExpression, "resultQueryValue", timeout = TimeSpan.FromSeconds 30.0)
        let! _ = service.ExecuteOperation(ExecuteCode, "let resultQueryValue = 11", timeout = TimeSpan.FromSeconds 30.0)
        let! second = service.ExecuteOperation(EvaluateExpression, "resultQueryValue", timeout = TimeSpan.FromSeconds 30.0)

        let singleJson = McpResultTools.GetFsiResult(service, "default-agent", first.ResultId)
        let listJson = McpResultTools.ListFsiResults(service, "default-agent")

        let mapJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "map",
                $"{first.ResultId}\n{second.ResultId}",
                queryText = "value"
            )

        let compareJson =
            McpResultTools.CompareFsiResults(
                service,
                "default-agent",
                first.ResultId,
                second.ResultId,
                queryText = "value"
            )

        let filterMaterializedJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "filter",
                $"{first.ResultId}\n{second.ResultId}",
                queryText = "isSuccess",
                materialization = "syntheticResult"
            )

        let resultResource = ResultResources(service)
        let resultResourceJson = resultResource.Result(first.ResultId)
        let agentResultsJson = resultResource.AgentResults("default-agent")
        let sessionResultsJson = resultResource.SessionResults("default-host", "default-session")

        let single = JsonSerializer.Deserialize<FsiExecutionRecord option>(singleJson)
        let listed = JsonSerializer.Deserialize<FsiExecutionRecord list>(listJson)
        let mapResponse = JsonSerializer.Deserialize<ResultQueryResponse>(mapJson)
        let compareResponse = JsonSerializer.Deserialize<ResultQueryResponse>(compareJson)
        let materializedResponse = JsonSerializer.Deserialize<ResultQueryResponse>(filterMaterializedJson)
        let synthetic = materializedResponse.ProducedResultIds |> List.head |> fun resultId -> service.TryGetResult(resultId)

        Assert.True(single.IsSome)
        Assert.Equal(first.ResultId, single.Value.ResultId)
        Assert.True(listed |> List.exists (fun value -> value.ResultId = first.ResultId))
        Assert.True(listed |> List.exists (fun value -> value.ResultId = second.ResultId))
        Assert.True(mapResponse.IsSuccess)
        Assert.Equal("[\"10\",\"11\"]", mapResponse.MaterializedJson.Value)
        Assert.True(compareResponse.IsSuccess)
        Assert.Contains(first.ResultId, compareResponse.MaterializedJson.Value)
        Assert.Contains(second.ResultId, compareResponse.MaterializedJson.Value)
        Assert.True(materializedResponse.IsSuccess)
        Assert.Single(materializedResponse.ProducedResultIds) |> ignore
        Assert.True(synthetic.IsSome)
        Assert.Equal(ResultQuery, synthetic.Value.OperationKind)
        Assert.Contains(first.ResultId, resultResourceJson)
        Assert.Contains(first.ResultId, agentResultsJson)
        Assert.Contains(second.ResultId, sessionResultsJson)
    }
