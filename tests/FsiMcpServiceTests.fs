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

[<Fact>]
let ``FsiMcpService output subscriber broker tracks subscribers on default route`` () =
    let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
    use _cleanup = service :> IDisposable

    let subscription = service.SubscribeSessionOutput("ui-reader", fromSequenceNo = 3L, includeHistory = true)
    let subscribers = service.ListSessionOutputSubscribers()

    Assert.Equal("default-session", subscription.SessionId)
    Assert.Equal("ui-reader", subscription.SubscriberId)
    Assert.Equal(3L, subscription.FromSequenceNo)
    Assert.True(subscription.IncludeHistory)
    Assert.Single(subscribers) |> ignore

[<Fact>]
let ``FsiMcpService output subscriber broker publishes monotonic sequence and supports unsubscribe`` () =
    let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
    use _cleanup = service :> IDisposable

    let _ = service.SubscribeSessionOutput("ui-reader")
    let firstEvent, firstSubscribers = service.PublishSessionOutput("stdout", "hello", executionId = "exec-1")
    let secondEvent, secondSubscribers = service.PublishSessionOutput("stdout", "world", executionId = "exec-1")
    let removed = service.UnsubscribeSessionOutput("ui-reader")
    let thirdEvent, thirdSubscribers = service.PublishSessionOutput("stdout", "bye", executionId = "exec-1")

    Assert.Equal(1L, firstEvent.SequenceNo)
    Assert.Equal(2L, secondEvent.SequenceNo)
    Assert.Equal(3L, thirdEvent.SequenceNo)
    Assert.Single(firstSubscribers) |> ignore
    Assert.Single(secondSubscribers) |> ignore
    Assert.True(removed)
    Assert.Empty(thirdSubscribers)

[<Fact>]
let ``FsiMcpService unified session output read returns archived events through same API`` () =
    let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
    use _cleanup = service :> IDisposable

    let _ = service.PublishSessionOutput("stdout", "alpha", executionId = "exec-archive-1")
    let _ = service.PublishSessionOutput("stderr", "beta", executionId = "exec-archive-1")
    let archive = service.SealSessionOutputArchive()
    let events = service.ListSessionOutput()
    let eventsAfter = service.ListSessionOutput(afterSequenceNo = 1L)

    Assert.Equal("default-session", archive.SessionId)
    Assert.Equal(2, archive.EventCount)
    Assert.Equal(Some 2L, archive.MaxSequenceNo)
    Assert.Equal(2, events.Length)
    Assert.Equal<int64 array>([| 1L; 2L |], events |> List.map (fun eventRecord -> eventRecord.SequenceNo) |> List.toArray)
    Assert.Single(eventsAfter) |> ignore
    Assert.Equal("beta", eventsAfter[0].Payload)

[<Fact>]
let ``FsiMcpService reset seals session output into archive before lifecycle reset`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let _ = service.PublishSessionOutput("stdout", "before-reset", executionId = "exec-reset-1")
        let _ = service.PublishSessionOutput("stderr", "before-reset-err", executionId = "exec-reset-1")
        let! _ = service.ExecuteOperation(ResetSession, "", timeout = TimeSpan.FromSeconds 30.0)

        let archive = service.TryGetSessionOutputArchive()
        let events = service.ListSessionOutput()

        Assert.True(archive.IsSome)
        Assert.Equal(2, archive.Value.EventCount)
        Assert.Equal(2, events.Length)
        Assert.Equal("before-reset", events[0].Payload)
        Assert.Equal("before-reset-err", events[1].Payload)
    }
