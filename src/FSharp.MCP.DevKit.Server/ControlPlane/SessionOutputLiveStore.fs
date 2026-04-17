namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.IO
open System.Collections.Concurrent
open System.Text
open FSharp.MCP.DevKit.Core

type JsonLineSessionOutputLiveStore(?executionStoreRoot: string) =
    let executionStoreRoot =
        executionStoreRoot
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace(value)))
        |> Option.defaultWith SessionOutputArchivePath.resolveExecutionStoreRoot

    let liveRoot = Path.Combine(executionStoreRoot, "output", "live")
    let sessionLocks = ConcurrentDictionary<string, obj>()

    let ensureDirectories () =
        Directory.CreateDirectory(liveRoot) |> ignore

    let sessionFilePath (sessionId: string) =
        Path.Combine(liveRoot, $"{SessionOutputArchivePath.normalizePathToken sessionId}.jsonl")

    let sessionGate (sessionId: string) = sessionLocks.GetOrAdd(sessionId, fun _ -> obj ())

    member _.ExecutionStoreRoot = executionStoreRoot

    interface ISessionOutputLiveStore with
        member _.Append(eventRecord: OutputEventRecord) =
            ensureDirectories ()
            let path = sessionFilePath eventRecord.SessionId
            let line = FSharpJson.serialize eventRecord + Environment.NewLine

            lock (sessionGate eventRecord.SessionId) (fun () ->
                File.AppendAllText(path, line, Encoding.UTF8))

        member _.ListEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            let afterSequenceNo = defaultArg afterSequenceNo 0L
            let limit = defaultArg limit Int32.MaxValue
            let path = sessionFilePath sessionId

            if File.Exists(path) then
                File.ReadLines(path)
                |> Seq.filter (fun line -> not (String.IsNullOrWhiteSpace(line)))
                |> Seq.map FSharpJson.deserialize<OutputEventRecord>
                |> Seq.filter (fun eventRecord -> eventRecord.SequenceNo > afterSequenceNo)
                |> Seq.sortBy (fun eventRecord -> eventRecord.SequenceNo)
                |> Seq.groupBy (fun eventRecord -> eventRecord.SequenceNo)
                |> Seq.map (fun (_, grouped) -> grouped |> Seq.last)
                |> Seq.truncate limit
                |> Seq.toList
            else
                []

        member _.ClearSession(sessionId: string) =
            let path = sessionFilePath sessionId

            lock (sessionGate sessionId) (fun () ->
                if File.Exists(path) then
                    File.Delete(path))

type SessionOutputStore
    (
        ?outputSubscriberBroker: IOutputSubscriberBroker,
        ?sessionOutputLiveStore: ISessionOutputLiveStore
    ) =
    let outputSubscriberBroker =
        defaultArg outputSubscriberBroker (InMemoryOutputSubscriberBroker() :> IOutputSubscriberBroker)

    let sessionOutputLiveStore =
        defaultArg sessionOutputLiveStore (JsonLineSessionOutputLiveStore() :> ISessionOutputLiveStore)

    interface IOutputStore with
        member _.Subscribe(record: OutputSubscriberRecord) =
            outputSubscriberBroker.Subscribe(record)

        member _.Unsubscribe(sessionId: string, subscriberId: string) =
            outputSubscriberBroker.Unsubscribe(sessionId, subscriberId)

        member _.ListSubscribers(sessionId: string) =
            outputSubscriberBroker.ListSubscribers(sessionId)

        member _.Publish(record: OutputEventRecord) =
            let eventRecord, subscribers = outputSubscriberBroker.Publish(record)
            sessionOutputLiveStore.Append(eventRecord)
            eventRecord, subscribers

        member _.ListEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            let afterSequenceNo = defaultArg afterSequenceNo 0L
            let limit = defaultArg limit Int32.MaxValue

            [ outputSubscriberBroker.ListEvents(sessionId, afterSequenceNo = afterSequenceNo)
              sessionOutputLiveStore.ListEvents(sessionId, afterSequenceNo = afterSequenceNo) ]
            |> List.concat
            |> List.sortBy (fun eventRecord -> eventRecord.SequenceNo)
            |> List.groupBy (fun eventRecord -> eventRecord.SequenceNo)
            |> List.map (fun (_, grouped) -> grouped |> List.last)
            |> List.truncate limit

        member _.ClearSession(sessionId: string) =
            let cleared = outputSubscriberBroker.ClearSessionEvents(sessionId)
            sessionOutputLiveStore.ClearSession(sessionId)
            cleared
