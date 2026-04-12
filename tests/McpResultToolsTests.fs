module McpResultToolsTests

open System
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.ControlPlane
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
        let listJson = McpResultTools.ListFsiResults(service, "default-agent", "", "")

        let mapJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "map",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "value",
                "",
                ""
            )

        let compareJson =
            McpResultTools.CompareFsiResults(
                service,
                "default-agent",
                first.ResultId,
                second.ResultId,
                "value",
                ""
            )

        let fsharpJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "map",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "records1 |> Seq.map (fun record -> record.Result.Value |> Option.defaultValue \"\") |> Seq.toList",
                "fsharpCode",
                ""
            )

        let filterMaterializedJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "filter",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "isSuccess",
                "",
                "syntheticResult"
            )

        let resultResource = ResultResources(service)
        let resultResourceJson = resultResource.Result(first.ResultId)
        let agentResultsJson = resultResource.AgentResults("default-agent")
        let sessionResultsJson = resultResource.SessionResults("default-host", "default-session")

        let single = FSharpJson.deserialize<FsiExecutionRecord option> singleJson
        let listed = FSharpJson.deserialize<FsiExecutionRecord list> listJson
        let mapResponse = FSharpJson.deserialize<ResultQueryResponse> mapJson
        let compareResponse = FSharpJson.deserialize<ResultQueryResponse> compareJson
        let fsharpResponse = FSharpJson.deserialize<ResultQueryResponse> fsharpJson
        let materializedResponse = FSharpJson.deserialize<ResultQueryResponse> filterMaterializedJson
        let synthetic = materializedResponse.ProducedResultIds |> List.head |> fun resultId -> service.TryGetResult(resultId)

        Assert.True(single.IsSome)
        Assert.Equal(first.ResultId, single.Value.ResultId)
        Assert.True(listed |> List.exists (fun value -> value.ResultId = first.ResultId))
        Assert.True(listed |> List.exists (fun value -> value.ResultId = second.ResultId))
        Assert.True(mapResponse.IsSuccess)
        Assert.Equal("[\"10\",\"11\"]", mapResponse.MaterializedJson.Value)
        Assert.True(compareResponse.IsSuccess)
        Assert.True(compareResponse.MaterializedJson.IsSome)
        Assert.Contains("\"leftValue\":\"10\"", compareResponse.MaterializedJson.Value)
        Assert.Contains("\"rightValue\":\"11\"", compareResponse.MaterializedJson.Value)
        Assert.True(fsharpResponse.IsSuccess)
        Assert.Equal("[\"10\",\"11\"]", fsharpResponse.MaterializedJson.Value)
        Assert.True(materializedResponse.IsSuccess)
        Assert.Single(materializedResponse.ProducedResultIds) |> ignore
        Assert.True(synthetic.IsSome)
        Assert.Equal(ResultQuery, synthetic.Value.OperationKind)
        Assert.Contains(first.ResultId, resultResourceJson)
        Assert.Contains(first.ResultId, agentResultsJson)
        Assert.Contains(second.ResultId, sessionResultsJson)
    }

[<Fact>]
let ``McpResultTools output tools and resources expose live broker state`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()

        let subscribeJson =
            McpResultTools.SubscribeSessionOutput(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "mgmt2-reader",
                0L,
                true
            )

        let _ = service.PublishSessionOutput("stdout", "alpha", executionId = "exec-out-1")
        let _ = service.PublishSessionOutput("stderr", "beta", executionId = "exec-out-1")

        let subscribersJson =
            McpResultTools.ListSessionOutputSubscribers(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let outputJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                0L,
                0
            )

        let outputAfterJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                1L,
                0
            )

        let resultResource = ResultResources(service)
        let outputResourceJson = resultResource.SessionOutput("default-host", "default-session")
        let outputAfterResourceJson = resultResource.SessionOutputAfter("default-host", "default-session", 1L)
        let subscribersResourceJson = resultResource.SessionOutputSubscribers("default-host", "default-session")

        let subscribed = FSharpJson.deserialize<OutputSubscriberRecord> subscribeJson
        let subscribers = FSharpJson.deserialize<OutputSubscriberRecord list> subscribersJson
        let events = FSharpJson.deserialize<OutputEventRecord list> outputJson
        let eventsAfter = FSharpJson.deserialize<OutputEventRecord list> outputAfterJson
        let resourceEvents = FSharpJson.deserialize<OutputEventRecord list> outputResourceJson
        let resourceEventsAfter = FSharpJson.deserialize<OutputEventRecord list> outputAfterResourceJson
        let resourceSubscribers = FSharpJson.deserialize<OutputSubscriberRecord list> subscribersResourceJson

        Assert.Equal("mgmt2-reader", subscribed.SubscriberId)
        Assert.Single(subscribers) |> ignore
        Assert.Equal(2, events.Length)
        Assert.Equal(1L, events[0].SequenceNo)
        Assert.Equal(2L, events[1].SequenceNo)
        Assert.Single(eventsAfter) |> ignore
        Assert.Equal("beta", eventsAfter[0].Payload)
        Assert.Equal(events.Length, resourceEvents.Length)
        Assert.Single(resourceEventsAfter) |> ignore
        Assert.Single(resourceSubscribers) |> ignore
        let unsubscribeJson =
            McpResultTools.UnsubscribeSessionOutput(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "mgmt2-reader"
            )

        let unsubscribed = FSharpJson.deserialize<bool> unsubscribeJson
        Assert.True(unsubscribed)
    }
