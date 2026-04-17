module SessionOutputLiveStoreTests

open System
open System.IO
open Xunit
open FSharp.MCP.DevKit.Server.ControlPlane

let private mkLiveEvent sessionId sequenceNo payload =
    { SessionId = sessionId
      ExecutionId = Some "exec-live-store"
      SequenceNo = sequenceNo
      StreamKind = "stdout"
      TimestampUtc = DateTime.UtcNow
      Payload = payload
      IsReplay = false }

[<Fact>]
let ``JsonLineSessionOutputLiveStore persists appended events for reload`` () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.SessionOutputLiveStoreTests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore

    let sessionId = "session-live-01"
    let store = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore

    store.Append(mkLiveEvent sessionId 1L "alpha")
    store.Append(mkLiveEvent sessionId 2L "beta")

    let reloadedStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
    let events = reloadedStore.ListEvents(sessionId)

    Assert.Equal(2, events.Length)
    Assert.Equal<int64 array>([| 1L; 2L |], events |> List.map (fun eventRecord -> eventRecord.SequenceNo) |> List.toArray)
    Assert.Equal("alpha", events[0].Payload)
    Assert.Equal("beta", events[1].Payload)

[<Fact>]
let ``JsonLineSessionOutputLiveStore clear session removes live file`` () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.SessionOutputLiveStoreTests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore

    let sessionId = "session-live-02"
    let store = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
    let path =
        Path.Combine(
            tempRoot,
            "output",
            "live",
            $"{SessionOutputArchivePath.normalizePathToken sessionId}.jsonl"
        )

    store.Append(mkLiveEvent sessionId 1L "alpha")
    Assert.True(File.Exists(path))

    store.ClearSession(sessionId)

    Assert.False(File.Exists(path))
    Assert.Empty(store.ListEvents(sessionId))

[<Fact>]
let ``SessionOutputStore publishes to subscribers and persists live events`` () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.SessionOutputLiveStoreTests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore

    let sessionId = "session-output-store-01"
    let broker = InMemoryOutputSubscriberBroker() :> IOutputSubscriberBroker
    let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
    let outputStore = SessionOutputStore(broker, liveStore) :> IOutputStore

    let _ =
        outputStore.Subscribe(
            { SessionId = sessionId
              SubscriberId = "ui-reader"
              FromSequenceNo = 0L
              IncludeHistory = true
              SubscribedAt = DateTime.UtcNow }
        )

    let eventRecord, subscribers = outputStore.Publish(mkLiveEvent sessionId 0L "alpha")
    let events = outputStore.ListEvents(sessionId)
    let persistedEvents =
        (JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore).ListEvents(sessionId)
    let cleared = outputStore.ClearSession(sessionId)

    Assert.Equal(1L, eventRecord.SequenceNo)
    Assert.Single(subscribers) |> ignore
    Assert.Single(events) |> ignore
    Assert.Single(persistedEvents) |> ignore
    Assert.Equal("alpha", events[0].Payload)
    Assert.Equal("alpha", persistedEvents[0].Payload)
    Assert.Equal(1, cleared)
    Assert.Empty(outputStore.ListEvents(sessionId))
