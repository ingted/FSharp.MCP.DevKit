module McpExecutionToolsTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.McpFsiTools

let private waitForCompletion (service: FsiMcpService) asyncId =
    task {
        let mutable attempt = 0
        let mutable status = service.GetAsyncExecutionStatus(asyncId)

        while not status.IsCompleted && attempt < 50 do
            do! Task.Delay(100)
            attempt <- attempt + 1
            status <- service.GetAsyncExecutionStatus(asyncId)

        return status
    }

[<Fact>]
let ``McpExecutionTools execute evaluate reset and async on explicit default route work`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! _ = service.ExecuteOperation(FSharp.MCP.DevKit.Core.ExecuteCode, "let routedBootstrap = 40", timeout = TimeSpan.FromSeconds 30.0)

        let! execOutput =
            McpExecutionTools.ExecuteFSharpCodeRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "let routedExplicit = 77"
            )

        let! evalOutput =
            McpExecutionTools.EvaluateFSharpExpressionRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "routedExplicit",
                30
            )

        let! addPathOutput =
            McpExecutionTools.AddSearchPathRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "/tmp/routed-explicit",
                30
            )

        let! stateOutput =
            McpExecutionTools.GetFsiStateRouted(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let! asyncId =
            McpExecutionTools.ExecuteFSharpCodeAsyncRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "let routedAsyncValue = 88",
                30
            )

        let! asyncStatus = waitForCompletion service asyncId

        let! resetOutput =
            McpExecutionTools.ResetFsiSessionRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                30
            )

        let! postResetEval =
            McpExecutionTools.EvaluateFSharpExpressionRouted(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "routedExplicit",
                30
            )

        Assert.Contains("routedExplicit", execOutput)
        Assert.Equal("77", evalOutput)
        Assert.Equal("Search path added successfully: /tmp/routed-explicit", addPathOutput)
        Assert.Contains("routedExplicit", stateOutput)
        Assert.True(asyncStatus.Exists)
        Assert.True(asyncStatus.IsCompleted)
        Assert.Equal(Some "default-agent", asyncStatus.AgentId)
        Assert.Equal(Some "default-host", asyncStatus.HostId)
        Assert.Equal(Some "default-session", asyncStatus.SessionId)
        Assert.Equal("FSI session reset successfully", resetOutput)
        Assert.Contains("routedExplicit", postResetEval)
        Assert.Contains("not defined", postResetEval, StringComparison.OrdinalIgnoreCase)
    }
