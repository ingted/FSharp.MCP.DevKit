module FsiMcpServiceTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open FSharp.MCP.DevKit.Core
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
let ``FsiMcpService executes through default routed in-proc path and stores results`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! _ = service.ExecuteOperation(ExecuteCode, "let serviceValue = 7", timeout = TimeSpan.FromSeconds 30.0)
        let! evalRecord = service.ExecuteOperation(EvaluateExpression, "serviceValue", timeout = TimeSpan.FromSeconds 30.0)

        let route = service.ResolveRoute()
        let results = service.ListSessionResults(route)

        Assert.True(evalRecord.Result.IsSuccess)
        Assert.Equal(Some "7", evalRecord.Result.Value)
        Assert.True(results.Length >= 2)
        Assert.True(results |> List.exists (fun record -> record.ResultId = evalRecord.ResultId))
    }

[<Fact>]
let ``FsiMcpService async queue completes and exposes status`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let asyncId = service.EnqueueExecuteCode("let asyncValue = 21", TimeSpan.FromSeconds 30.0)
        let! status = waitForCompletion service asyncId
        let! evalRecord = service.ExecuteOperation(EvaluateExpression, "asyncValue", timeout = TimeSpan.FromSeconds 30.0)

        Assert.True(status.Exists)
        Assert.True(status.IsCompleted)
        Assert.True(status.ResultId.IsSome)
        Assert.Equal(Some "default-agent", status.AgentId)
        Assert.Equal(Some "default-host", status.HostId)
        Assert.Equal(Some "default-session", status.SessionId)
        Assert.True(status.Result.IsSome)
        Assert.True(evalRecord.Result.IsSuccess)
        Assert.Equal(Some "21", evalRecord.Result.Value)
    }
