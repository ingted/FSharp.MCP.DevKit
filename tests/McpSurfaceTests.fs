module McpSurfaceTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open FSharp.MCP.DevKit.Core
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
let ``FSharpInteractiveTools execute evaluate add-path and state use routed service`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! execResult = FSharpInteractiveTools.ExecuteFSharpCode(service, "let toolValue = 31", 30)
        let! evalResult = FSharpInteractiveTools.EvaluateFSharpExpression(service, "toolValue", 30)
        let! addPathResult = FSharpInteractiveTools.AddSearchPath(service, "/tmp", 30)
        let! stateResult = FSharpInteractiveTools.GetFSIState(service, 30)

        Assert.Contains("toolValue", execResult)
        Assert.Equal("31", evalResult)
        Assert.Equal("Search path added successfully: /tmp", addPathResult)
        Assert.Contains("FSI Session State", stateResult)
    }

[<Fact>]
let ``FSharpInteractiveTools detailed error includes routed execution metadata`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! detail = FSharpInteractiveTools.ExecuteFSharpCodeDetailed(service, "missingValue", 30)

        Assert.Contains("=== EXECUTION FAILED ===", detail)
        Assert.Contains("BackendKind: InProc", detail)
        Assert.Contains("SessionId: default-session", detail)
    }

[<Fact>]
let ``Fsi async status resource reflects async tool completion`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! asyncId = FSharpInteractiveTools.ExecuteFSharpCodeAsync(service, "let resourceAsyncValue = 44", 30)
        let! _ = waitForCompletion service asyncId
        let resource = FSharp.MCP.DevKit.Server.Program.FsiResources(service)
        let json = resource.AsyncStatus(asyncId)
        let status = FSharpJson.deserialize<AsyncFsiStatusDto> json

        Assert.Equal(asyncId, status.AsyncId)
        Assert.True(status.Exists)
        Assert.True(status.IsCompleted)
        Assert.True(status.ResultId.IsSome)
        Assert.Equal(Some "default-agent", status.AgentId)
        Assert.Equal(Some "default-host", status.HostId)
        Assert.Equal(Some "default-session", status.SessionId)
        Assert.True(status.Result.IsSome)
    }
