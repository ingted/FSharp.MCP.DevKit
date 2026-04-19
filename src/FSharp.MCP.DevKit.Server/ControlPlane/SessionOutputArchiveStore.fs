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

type SessionOutputSealPendingIndex =
    { SessionId: string
      PendingAt: DateTime
      EventCount: int
      MaxSequenceNo: int64 option
      ErrorMessage: string
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
    let pendingRoot = Path.Combine(executionStoreRoot, "output", "seal-pending")
    let pendingIndexRoot = Path.Combine(executionStoreRoot, "seal-pending-index")

    let archives = ConcurrentDictionary<string, SessionOutputArchiveRecord * OutputEventRecord array>()
    let pendings = ConcurrentDictionary<string, SessionOutputSealPendingRecord * OutputEventRecord array>()

    let ensureDirectories () =
        Directory.CreateDirectory(archiveRoot) |> ignore
        Directory.CreateDirectory(archiveIndexRoot) |> ignore
        Directory.CreateDirectory(pendingRoot) |> ignore
        Directory.CreateDirectory(pendingIndexRoot) |> ignore

    let sessionArchiveDirectory (sessionId: string) =
        Path.Combine(archiveRoot, SessionOutputArchivePath.normalizePathToken sessionId)

    let sessionArchiveIndexPath (sessionId: string) =
        Path.Combine(archiveIndexRoot, $"{SessionOutputArchivePath.normalizePathToken sessionId}.json")

    let sessionPendingDirectory (sessionId: string) =
        Path.Combine(pendingRoot, SessionOutputArchivePath.normalizePathToken sessionId)

    let sessionPendingIndexPath (sessionId: string) =
        Path.Combine(pendingIndexRoot, $"{SessionOutputArchivePath.normalizePathToken sessionId}.json")

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

    let toPendingIndex (record: SessionOutputSealPendingRecord) (segments: string list) : SessionOutputSealPendingIndex =
        { SessionId = record.SessionId
          PendingAt = record.PendingAt
          EventCount = record.EventCount
          MaxSequenceNo = record.MaxSequenceNo
          ErrorMessage = record.ErrorMessage
          Segments = segments }

    let toPendingRecord (index: SessionOutputSealPendingIndex) : SessionOutputSealPendingRecord =
        { SessionId = index.SessionId
          PendingAt = index.PendingAt
          EventCount = index.EventCount
          MaxSequenceNo = index.MaxSequenceNo
          ErrorMessage = index.ErrorMessage }

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

    let tryLoadPending sessionId =
        let indexPath = sessionPendingIndexPath sessionId

        if File.Exists(indexPath) then
            let index = File.ReadAllText(indexPath) |> FSharpJson.deserialize<SessionOutputSealPendingIndex>
            let events = loadEventsFromSegments index.Segments
            let record = toPendingRecord index
            pendings.[sessionId] <- (record, events)
            Some(record, events)
        else
            None

    let listArchiveIndexSessionIds () =
        ensureDirectories ()

        Directory.EnumerateFiles(archiveIndexRoot, "*.json", SearchOption.TopDirectoryOnly)
        |> Seq.choose (fun path ->
            try
                let index = File.ReadAllText(path) |> FSharpJson.deserialize<SessionOutputArchiveIndex>
                Some index.SessionId
            with _ ->
                None)
        |> Seq.distinct
        |> Seq.toList

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

    let persistPending sessionId (record: SessionOutputSealPendingRecord) (events: OutputEventRecord array) =
        ensureDirectories ()

        let pendingDirectory = sessionPendingDirectory sessionId
        Directory.CreateDirectory(pendingDirectory) |> ignore

        let segmentFileName = $"{record.PendingAt:yyyyMMddHHmmssfff}.{events.Length:D5}.jsonl"
        let segmentPath = Path.Combine(pendingDirectory, segmentFileName)

        events
        |> Array.map FSharpJson.serialize
        |> fun lines -> File.WriteAllLines(segmentPath, lines)

        let indexPath = sessionPendingIndexPath sessionId
        let index = toPendingIndex record [ segmentPath ]
        File.WriteAllText(indexPath, FSharpJson.serialize index)

    let clearPendingArtifacts sessionId =
        let pendingDirectory = sessionPendingDirectory sessionId
        let pendingIndexPath = sessionPendingIndexPath sessionId

        if Directory.Exists(pendingDirectory) then
            Directory.Delete(pendingDirectory, true)

        if File.Exists(pendingIndexPath) then
            File.Delete(pendingIndexPath)

        pendings.TryRemove(sessionId) |> ignore

    let deleteArchiveArtifacts sessionId =
        let archiveDirectory = sessionArchiveDirectory sessionId
        let archiveIndexPath = sessionArchiveIndexPath sessionId

        if Directory.Exists(archiveDirectory) then
            Directory.Delete(archiveDirectory, true)

        if File.Exists(archiveIndexPath) then
            File.Delete(archiveIndexPath)

        archives.TryRemove(sessionId) |> ignore

    let candidateReason (keepLatest: int option) (olderThanUtc: DateTime option) =
        [ match keepLatest with
          | Some value -> $"outside keepLatest={value}"
          | None -> ()

          match olderThanUtc with
          | Some value -> $"archivedAt < {value:O}"
          | None -> () ]
        |> String.concat "; "

    let pruneCandidates (keepLatest: int option) (olderThanUtc: DateTime option) =
        let archives =
            listArchiveIndexSessionIds ()
            |> List.choose (fun sessionId ->
                match tryLoadArchive sessionId with
                | Some (record, _) -> Some record
                | None ->
                    match archives.TryGetValue sessionId with
                    | true, (record, _) -> Some record
                    | false, _ -> None)
            |> List.sortByDescending (fun record -> record.ArchivedAt)

        archives
        |> List.mapi (fun index record -> index, record)
        |> List.filter (fun (index, record) ->
            let keepLatestAllows =
                match keepLatest with
                | Some value -> index >= value
                | None -> true

            let olderThanAllows =
                match olderThanUtc with
                | Some value -> record.ArchivedAt.ToUniversalTime() < value.ToUniversalTime()
                | None -> true

            keepLatestAllows && olderThanAllows)
        |> List.map (fun (index, record) ->
            { SessionId = record.SessionId
              ArchivedAt = record.ArchivedAt
              EventCount = record.EventCount
              MaxSequenceNo = record.MaxSequenceNo
              Reason = candidateReason keepLatest olderThanUtc })

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
            clearPendingArtifacts sessionId
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

        member this.ListArchives(?limit: int) =
            let limit = defaultArg limit Int32.MaxValue

            let cached =
                archives.Values
                |> Seq.map fst

            let persisted =
                listArchiveIndexSessionIds ()
                |> Seq.choose (fun sessionId ->
                    match archives.TryGetValue sessionId with
                    | true, (record, _) -> Some record
                    | false, _ -> tryLoadArchive sessionId |> Option.map fst)

            Seq.append cached persisted
            |> Seq.groupBy (fun record -> record.SessionId)
            |> Seq.map (fun (_, records) -> records |> Seq.sortByDescending (fun record -> record.ArchivedAt) |> Seq.head)
            |> Seq.sortByDescending (fun record -> record.ArchivedAt)
            |> Seq.truncate limit
            |> Seq.toList

        member _.TryGetArchive(sessionId: string) =
            match archives.TryGetValue sessionId with
            | true, (record, _) -> Some record
            | false, _ ->
                tryLoadArchive sessionId
                |> Option.map fst

        member _.MarkSealPending(sessionId: string, events: OutputEventRecord list, pendingAt: DateTime, errorMessage: string) =
            let orderedEvents =
                events
                |> List.sortBy (fun eventRecord -> eventRecord.SequenceNo)
                |> List.groupBy (fun eventRecord -> eventRecord.SequenceNo)
                |> List.map (fun (_, grouped) -> grouped |> List.last)
                |> List.toArray

            let record =
                { SessionId = sessionId
                  PendingAt = if pendingAt = DateTime.MinValue then DateTime.UtcNow else pendingAt
                  EventCount = orderedEvents.Length
                  MaxSequenceNo = orderedEvents |> Array.tryLast |> Option.map (fun eventRecord -> eventRecord.SequenceNo)
                  ErrorMessage = errorMessage }

            persistPending sessionId record orderedEvents
            pendings.[sessionId] <- (record, orderedEvents)
            record

        member _.ListPendingEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            let afterSequenceNo = defaultArg afterSequenceNo 0L
            let limit = defaultArg limit Int32.MaxValue

            let events =
                match pendings.TryGetValue sessionId with
                | true, (_, cachedEvents) -> cachedEvents
                | false, _ ->
                    match tryLoadPending sessionId with
                    | Some (_, loadedEvents) -> loadedEvents
                    | None -> [||]

            events
            |> Array.filter (fun eventRecord -> eventRecord.SequenceNo > afterSequenceNo)
            |> Array.truncate limit
            |> Array.toList

        member _.TryGetSealPending(sessionId: string) =
            match pendings.TryGetValue sessionId with
            | true, (record, _) -> Some record
            | false, _ ->
                tryLoadPending sessionId
                |> Option.map fst

        member this.RecoverSealPending(sessionId: string) =
            let pendingAndEvents =
                match pendings.TryGetValue sessionId with
                | true, value -> Some value
                | false, _ -> tryLoadPending sessionId

            pendingAndEvents
            |> Option.map (fun (pending, events) ->
                let archiveRecord =
                    { SessionId = sessionId
                      ArchivedAt = DateTime.UtcNow
                      EventCount = events.Length
                      MaxSequenceNo = pending.MaxSequenceNo }

                persistArchive sessionId archiveRecord events
                archives.[sessionId] <- (archiveRecord, events)
                clearPendingArtifacts sessionId
                archiveRecord)

        member _.PruneArchives(?keepLatest: int, ?olderThanUtc: DateTime, ?dryRun: bool) =
            let dryRun = defaultArg dryRun true
            let keepLatest = keepLatest |> Option.filter (fun value -> value >= 0)

            match keepLatest, olderThanUtc with
            | None, None ->
                { DryRun = dryRun
                  KeepLatest = None
                  OlderThanUtc = None
                  CandidateCount = 0
                  DeletedCount = 0
                  Candidates = []
                  Errors = [ "At least one prune policy is required: keepLatest or olderThanUtc." ] }
            | _ ->
                let candidates = pruneCandidates keepLatest olderThanUtc

                if dryRun then
                    { DryRun = true
                      KeepLatest = keepLatest
                      OlderThanUtc = olderThanUtc
                      CandidateCount = candidates.Length
                      DeletedCount = 0
                      Candidates = candidates
                      Errors = [] }
                else
                    let deleted, errors =
                        candidates
                        |> List.fold
                            (fun (deleted, errors) candidate ->
                                try
                                    deleteArchiveArtifacts candidate.SessionId
                                    deleted + 1, errors
                                with ex ->
                                    deleted, $"{candidate.SessionId}: {ex.Message}" :: errors)
                            (0, [])

                    { DryRun = false
                      KeepLatest = keepLatest
                      OlderThanUtc = olderThanUtc
                      CandidateCount = candidates.Length
                      DeletedCount = deleted
                      Candidates = candidates
                      Errors = errors |> List.rev }
