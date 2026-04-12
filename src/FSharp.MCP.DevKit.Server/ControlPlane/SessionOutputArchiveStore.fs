namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Collections.Concurrent

type InMemorySessionOutputArchiveStore() =
    let archives = ConcurrentDictionary<string, SessionOutputArchiveRecord * OutputEventRecord array>()

    interface ISessionOutputArchiveStore with
        member _.Seal(sessionId: string, events: OutputEventRecord list, archivedAt: DateTime) =
            let orderedEvents =
                events
                |> List.sortBy (fun eventRecord -> eventRecord.SequenceNo)
                |> List.groupBy (fun eventRecord -> eventRecord.SequenceNo)
                |> List.map (fun (_, grouped) -> grouped |> List.last)
                |> List.toArray

            let record =
                { SessionId = sessionId
                  ArchivedAt =
                    if archivedAt = DateTime.MinValue then DateTime.UtcNow else archivedAt
                  EventCount = orderedEvents.Length
                  MaxSequenceNo = orderedEvents |> Array.tryLast |> Option.map (fun eventRecord -> eventRecord.SequenceNo) }

            archives.[sessionId] <- (record, orderedEvents)
            record

        member _.ListEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            let afterSequenceNo = defaultArg afterSequenceNo 0L
            let limit = defaultArg limit Int32.MaxValue

            match archives.TryGetValue sessionId with
            | true, (_, events) ->
                events
                |> Array.filter (fun eventRecord -> eventRecord.SequenceNo > afterSequenceNo)
                |> Array.truncate limit
                |> Array.toList
            | false, _ -> []

        member _.TryGetArchive(sessionId: string) =
            match archives.TryGetValue sessionId with
            | true, (record, _) -> Some record
            | false, _ -> None
