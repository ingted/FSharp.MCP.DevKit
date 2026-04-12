namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Collections.Concurrent
open System.Threading

type InMemoryOutputSubscriberBroker() =
    let subscribers = ConcurrentDictionary<string, ConcurrentDictionary<string, OutputSubscriberRecord>>()
    let nextSequenceBySession = ConcurrentDictionary<string, int64>()
    let eventsBySession = ConcurrentDictionary<string, ConcurrentQueue<OutputEventRecord>>()

    let getSessionBucket (sessionId: string) =
        subscribers.GetOrAdd(sessionId, fun _ -> ConcurrentDictionary<string, OutputSubscriberRecord>())

    let getEventBucket (sessionId: string) =
        eventsBySession.GetOrAdd(sessionId, fun _ -> ConcurrentQueue<OutputEventRecord>())

    interface IOutputSubscriberBroker with
        member _.Subscribe(record: OutputSubscriberRecord) =
            let normalized =
                { record with
                    FromSequenceNo = max 0L record.FromSequenceNo
                    SubscribedAt =
                        if record.SubscribedAt = DateTime.MinValue then DateTime.UtcNow else record.SubscribedAt }

            let bucket = getSessionBucket normalized.SessionId
            bucket.[normalized.SubscriberId] <- normalized
            normalized

        member _.Unsubscribe(sessionId: string, subscriberId: string) =
            let mutable bucket = Unchecked.defaultof<_>

            if subscribers.TryGetValue(sessionId, &bucket) then
                bucket.TryRemove(subscriberId) |> fst
            else
                false

        member _.ListSubscribers(sessionId: string) =
            let mutable bucket = Unchecked.defaultof<_>

            if subscribers.TryGetValue(sessionId, &bucket) then
                bucket.Values
                |> Seq.sortBy (fun record -> record.SubscriberId)
                |> Seq.toList
            else
                []

        member _.Publish(record: OutputEventRecord) =
            let sequenceNo =
                if record.SequenceNo > 0L then
                    nextSequenceBySession.AddOrUpdate(record.SessionId, record.SequenceNo, fun _ current -> max current record.SequenceNo)
                else
                    nextSequenceBySession.AddOrUpdate(record.SessionId, 1L, fun _ current -> current + 1L)

            let normalized =
                { record with
                    SequenceNo = sequenceNo
                    TimestampUtc =
                        if record.TimestampUtc = DateTime.MinValue then DateTime.UtcNow else record.TimestampUtc }

            let interestedSubscribers =
                let mutable bucket = Unchecked.defaultof<_>

                if subscribers.TryGetValue(normalized.SessionId, &bucket) then
                    bucket.Values
                    |> Seq.sortBy (fun subscriber -> subscriber.SubscriberId)
                    |> Seq.toList
                else
                    []
                |> List.filter (fun subscriber -> normalized.SequenceNo >= subscriber.FromSequenceNo)

            let eventBucket = getEventBucket normalized.SessionId
            eventBucket.Enqueue(normalized)

            normalized, interestedSubscribers

        member _.ListEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            let afterSequenceNo = defaultArg afterSequenceNo 0L
            let limit = defaultArg limit Int32.MaxValue
            let mutable bucket = Unchecked.defaultof<_>

            if eventsBySession.TryGetValue(sessionId, &bucket) then
                bucket.ToArray()
                |> Array.filter (fun eventRecord -> eventRecord.SequenceNo > afterSequenceNo)
                |> Array.sortBy (fun eventRecord -> eventRecord.SequenceNo)
                |> Array.truncate limit
                |> Array.toList
            else
                []
