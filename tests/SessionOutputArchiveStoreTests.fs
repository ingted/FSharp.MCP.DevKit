module SessionOutputArchiveStoreTests

open System
open System.IO
open Xunit
open FSharp.MCP.DevKit.Server.ControlPlane

let private mkEvent sessionId sequenceNo payload =
    { SessionId = sessionId
      ExecutionId = Some "exec-archive-store"
      SequenceNo = sequenceNo
      StreamKind = "stdout"
      TimestampUtc = DateTime.UtcNow
      Payload = payload
      IsReplay = false }

[<Fact>]
let ``JsonLineSessionOutputArchiveStore persists archive index and segment for reload`` () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.SessionOutputArchiveStoreTests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore

    let sessionId = "session-archive-01"
    let store = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

    let archive =
        store.Seal(
            sessionId,
            [ mkEvent sessionId 2L "beta"
              mkEvent sessionId 1L "alpha" ],
            DateTime(2026, 4, 13, 11, 0, 0, DateTimeKind.Utc)
        )

    let reloadedStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore
    let reloadedArchive = reloadedStore.TryGetArchive(sessionId)
    let reloadedEvents = reloadedStore.ListEvents(sessionId)
    let archiveIndexPath =
        Path.Combine(
            tempRoot,
            "archive-index",
            $"{SessionOutputArchivePath.normalizePathToken sessionId}.json"
        )

    Assert.Equal(2, archive.EventCount)
    Assert.True(File.Exists(archiveIndexPath))
    Assert.True(reloadedArchive.IsSome)
    Assert.Equal(Some 2L, reloadedArchive.Value.MaxSequenceNo)
    Assert.Equal(2, reloadedEvents.Length)
    Assert.Equal<int64 array>([| 1L; 2L |], reloadedEvents |> List.map (fun eventRecord -> eventRecord.SequenceNo) |> List.toArray)
    Assert.Equal("alpha", reloadedEvents[0].Payload)
    Assert.Equal("beta", reloadedEvents[1].Payload)

[<Fact>]
let ``JsonLineSessionOutputArchiveStore persists seal pending index and can recover into archive`` () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.SessionOutputArchiveStoreTests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore

    let sessionId = "session-pending-01"
    let store = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

    let pending =
        store.MarkSealPending(
            sessionId,
            [ mkEvent sessionId 2L "beta"
              mkEvent sessionId 1L "alpha" ],
            DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc),
            "seal failed"
        )

    let reloadedStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore
    let reloadedPending = reloadedStore.TryGetSealPending(sessionId)
    let pendingEvents = reloadedStore.ListPendingEvents(sessionId)
    let recovered = reloadedStore.RecoverSealPending(sessionId)
    let archiveEvents = reloadedStore.ListEvents(sessionId)

    Assert.Equal(2, pending.EventCount)
    Assert.True(reloadedPending.IsSome)
    Assert.Equal("seal failed", reloadedPending.Value.ErrorMessage)
    Assert.Equal(2, pendingEvents.Length)
    Assert.True(recovered.IsSome)
    Assert.Equal(2, recovered.Value.EventCount)
    Assert.Equal(2, archiveEvents.Length)
    Assert.True(reloadedStore.TryGetSealPending(sessionId).IsNone)
