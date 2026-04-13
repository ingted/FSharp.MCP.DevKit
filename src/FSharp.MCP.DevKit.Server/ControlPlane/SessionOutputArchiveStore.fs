namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.IO
open System.Collections.Concurrent
open FSharp.MCP.DevKit.Core

type SessionOutputArchiveIndex =
    { SessionId: string
      ArchivedAt: DateTime
      EventCount: int
      MaxSequenceNo: int64 option
      Segments: string list }

module SessionOutputArchivePath =

    let private tryFindRepoRootWithMisc (startPath: string) =
        if String.IsNullOrWhiteSpace(startPath) then
            None
        else
            let mutable current = DirectoryInfo(startPath)
            let mutable result = None
            let mutable remaining = 8

            while remaining > 0 && not (isNull current) && result.IsNone do
                let miscPath = Path.Combine(current.FullName, "misc")

                if Directory.Exists(miscPath) then
                    result <- Some current.FullName
                else
                    current <- current.Parent
                    remaining <- remaining - 1

            result

    let resolveExecutionStoreRoot () =
        let configured = Environment.GetEnvironmentVariable("PULSETRADE_EXECUTION_STORE_ROOT")

        if not (String.IsNullOrWhiteSpace(configured)) then
            configured
        else
            [ Directory.GetCurrentDirectory()
              AppContext.BaseDirectory ]
            |> List.choose tryFindRepoRootWithMisc
            |> List.tryHead
            |> Option.map (fun repoRoot -> Path.Combine(repoRoot, "misc", "execution-store"))
            |> Option.defaultWith (fun () -> Path.Combine(Directory.GetCurrentDirectory(), "misc", "execution-store"))

    let normalizePathToken (value: string) =
        let invalidChars = Path.GetInvalidFileNameChars() |> Set.ofArray

        value
        |> Seq.map (fun ch -> if invalidChars.Contains(ch) then '_' else ch)
        |> Array.ofSeq
        |> String

type JsonLineSessionOutputArchiveStore(?executionStoreRoot: string) =
    let executionStoreRoot =
        executionStoreRoot
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace(value)))
        |> Option.defaultWith SessionOutputArchivePath.resolveExecutionStoreRoot

    let archiveRoot = Path.Combine(executionStoreRoot, "output", "archive")
    let archiveIndexRoot = Path.Combine(executionStoreRoot, "archive-index")

    let archives = ConcurrentDictionary<string, SessionOutputArchiveRecord * OutputEventRecord array>()

    let ensureDirectories () =
        Directory.CreateDirectory(archiveRoot) |> ignore
        Directory.CreateDirectory(archiveIndexRoot) |> ignore

    let sessionArchiveDirectory (sessionId: string) =
        Path.Combine(archiveRoot, SessionOutputArchivePath.normalizePathToken sessionId)

    let sessionArchiveIndexPath (sessionId: string) =
        Path.Combine(archiveIndexRoot, $"{SessionOutputArchivePath.normalizePathToken sessionId}.json")

    let toIndex (record: SessionOutputArchiveRecord) (segments: string list) : SessionOutputArchiveIndex =
        { SessionId = record.SessionId
          ArchivedAt = record.ArchivedAt
          EventCount = record.EventCount
          MaxSequenceNo = record.MaxSequenceNo
          Segments = segments }

    let toRecord (index: SessionOutputArchiveIndex) : SessionOutputArchiveRecord =
        { SessionId = index.SessionId
          ArchivedAt = index.ArchivedAt
          EventCount = index.EventCount
          MaxSequenceNo = index.MaxSequenceNo }

    let loadEventsFromSegments (segments: string list) =
        segments
        |> List.collect (fun path ->
            if File.Exists(path) then
                File.ReadLines(path)
                |> Seq.filter (fun line -> not (String.IsNullOrWhiteSpace(line)))
                |> Seq.map FSharpJson.deserialize<OutputEventRecord>
                |> Seq.toList
            else
                [])
        |> List.sortBy (fun eventRecord -> eventRecord.SequenceNo)
        |> List.groupBy (fun eventRecord -> eventRecord.SequenceNo)
        |> List.map (fun (_, grouped) -> grouped |> List.last)
        |> List.toArray

    let tryLoadArchive sessionId =
        let indexPath = sessionArchiveIndexPath sessionId

        if File.Exists(indexPath) then
            let index = File.ReadAllText(indexPath) |> FSharpJson.deserialize<SessionOutputArchiveIndex>
            let events = loadEventsFromSegments index.Segments
            let record = toRecord index
            archives.[sessionId] <- (record, events)
            Some(record, events)
        else
            None

    let persistArchive sessionId (record: SessionOutputArchiveRecord) (events: OutputEventRecord array) =
        ensureDirectories ()

        let archiveDirectory = sessionArchiveDirectory sessionId
        Directory.CreateDirectory(archiveDirectory) |> ignore

        let segmentFileName = $"{record.ArchivedAt:yyyyMMddHHmmssfff}.{events.Length:D5}.jsonl"
        let segmentPath = Path.Combine(archiveDirectory, segmentFileName)

        events
        |> Array.map FSharpJson.serialize
        |> fun lines -> File.WriteAllLines(segmentPath, lines)

        let indexPath = sessionArchiveIndexPath sessionId
        let index = toIndex record [ segmentPath ]
        File.WriteAllText(indexPath, FSharpJson.serialize index)

    member _.ExecutionStoreRoot = executionStoreRoot

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

            persistArchive sessionId record orderedEvents
            archives.[sessionId] <- (record, orderedEvents)
            record

        member _.ListEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            let afterSequenceNo = defaultArg afterSequenceNo 0L
            let limit = defaultArg limit Int32.MaxValue

            let events =
                match archives.TryGetValue sessionId with
                | true, (_, cachedEvents) -> cachedEvents
                | false, _ ->
                    match tryLoadArchive sessionId with
                    | Some (_, loadedEvents) -> loadedEvents
                    | None -> [||]

            events
            |> Array.filter (fun eventRecord -> eventRecord.SequenceNo > afterSequenceNo)
            |> Array.truncate limit
            |> Array.toList

        member _.TryGetArchive(sessionId: string) =
            match archives.TryGetValue sessionId with
            | true, (record, _) -> Some record
            | false, _ ->
                tryLoadArchive sessionId
                |> Option.map fst
