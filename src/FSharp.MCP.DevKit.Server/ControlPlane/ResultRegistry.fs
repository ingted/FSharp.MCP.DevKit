namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.IO
open System.Text
open System.Collections.Concurrent
open FSharp.MCP.DevKit.Core

module ResultRegistryPath =

    let resultIndexRoot (executionStoreRoot: string) =
        Path.Combine(executionStoreRoot, "result-index")

    let agentResultPath (executionStoreRoot: string) (agentId: string) =
        Path.Combine(
            resultIndexRoot executionStoreRoot,
            $"{SessionOutputArchivePath.normalizePathToken agentId}.jsonl"
        )

module ExecutionStoreQuery =
    let private matchesOptional expected actual =
        match expected with
        | Some value -> String.Equals(value, actual, StringComparison.OrdinalIgnoreCase)
        | None -> true

    let private matchesMetadata (expected: (string * string) list option) (record: FsiExecutionRecord) =
        match expected with
        | None -> true
        | Some values ->
            values
            |> List.forall (fun (key, value) ->
                match record.Metadata |> Map.tryFind key with
                | Some actual -> String.Equals(actual, value, StringComparison.OrdinalIgnoreCase)
                | None -> false)

    let normalizeLimit limit =
        match limit with
        | Some value when value > 0 -> min value 500
        | _ -> 100

    let list
        (agentId: string option)
        (hostId: string option)
        (sessionId: string option)
        (metadata: (string * string) list option)
        (limit: int option)
        (records: seq<FsiExecutionRecord>)
        =
        records
        |> Seq.filter (fun record ->
            matchesOptional agentId record.AgentId
            && matchesOptional hostId record.HostId
            && matchesOptional sessionId record.SessionId
            && matchesMetadata metadata record)
        |> Seq.sortByDescending (fun record -> record.SubmittedAt)
        |> Seq.truncate (normalizeLimit limit)
        |> Seq.toList

module ExecutionStore =
    let ofResultRegistry (registry: IResultRegistry) =
        match registry with
        | :? IExecutionStore as store -> store
        | _ ->
            { new IExecutionStore with
                member _.Put(record: FsiExecutionRecord) =
                    registry.Put record

                member _.TryGet(resultId: string) =
                    registry.TryGet resultId

                member _.ListBySession(route: ExecutionRoute) =
                    registry.ListBySession route

                member _.ListBySessionId(sessionId: string) =
                    registry.ListBySessionId sessionId

                member _.ListByAgent(agentId: string) =
                    registry.ListByAgent agentId

                member _.List(?agentId: string, ?hostId: string, ?sessionId: string, ?metadata: (string * string) list, ?limit: int) =
                    let records =
                        match agentId, hostId, sessionId with
                        | Some agentId, Some hostId, Some sessionId ->
                            registry.ListBySession(
                                { AgentId = agentId
                                  HostId = hostId
                                  SessionId = sessionId }
                            )
                        | _, _, Some sessionId -> registry.ListBySessionId sessionId
                        | Some agentId, _, _ -> registry.ListByAgent agentId
                        | _ -> []

                    ExecutionStoreQuery.list agentId hostId sessionId metadata limit records }

type InMemoryResultRegistry() =
    let results = ConcurrentDictionary<string, FsiExecutionRecord>()

    interface IExecutionStore with
        member _.Put(record: FsiExecutionRecord) = results.[record.ResultId] <- record

        member _.TryGet(resultId: string) =
            match results.TryGetValue resultId with
            | true, record -> Some record
            | false, _ -> None

        member _.ListBySession(route: ExecutionRoute) =
            results.Values
            |> Seq.filter (fun record ->
                record.AgentId = route.AgentId
                && record.HostId = route.HostId
                && record.SessionId = route.SessionId)
            |> Seq.sortByDescending (fun record -> record.SubmittedAt)
            |> Seq.toList

        member _.ListBySessionId(sessionId: string) =
            results.Values
            |> Seq.filter (fun record -> record.SessionId = sessionId)
            |> Seq.sortByDescending (fun record -> record.SubmittedAt)
            |> Seq.toList

        member _.ListByAgent(agentId: string) =
            results.Values
            |> Seq.filter (fun record -> record.AgentId = agentId)
            |> Seq.sortByDescending (fun record -> record.SubmittedAt)
            |> Seq.toList

        member _.List(?agentId: string, ?hostId: string, ?sessionId: string, ?metadata: (string * string) list, ?limit: int) =
            ExecutionStoreQuery.list agentId hostId sessionId metadata limit results.Values

type private FsiExecutionRecordV1 =
    { ResultId: string
      RequestId: string
      AgentId: string
      BackendKind: BackendKind
      HostId: string
      SessionId: string
      OperationKind: OperationKind
      SubmittedAt: DateTime
      StartedAt: DateTime option
      CompletedAt: DateTime option
      RawErrorType: string option
      Result: FsiResult }

module private PersistedExecutionRecord =
    let deserialize (line: string) =
        try
            FSharpJson.deserialize<FsiExecutionRecord> line
        with _ ->
            let legacy = FSharpJson.deserialize<FsiExecutionRecordV1> line

            { ResultId = legacy.ResultId
              RequestId = legacy.RequestId
              AgentId = legacy.AgentId
              BackendKind = legacy.BackendKind
              HostId = legacy.HostId
              SessionId = legacy.SessionId
              OperationKind = legacy.OperationKind
              SubmittedAt = legacy.SubmittedAt
              StartedAt = legacy.StartedAt
              CompletedAt = legacy.CompletedAt
              RawErrorType = legacy.RawErrorType
              Metadata = Map.empty
              Result = legacy.Result }

type JsonLineResultRegistry(?executionStoreRoot: string) =
    static let fileGates = ConcurrentDictionary<string, obj>(StringComparer.OrdinalIgnoreCase)

    let executionStoreRoot =
        executionStoreRoot
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace(value)))
        |> Option.defaultWith SessionOutputArchivePath.resolveExecutionStoreRoot

    let results = ConcurrentDictionary<string, FsiExecutionRecord>()

    let ensureDirectories () =
        Directory.CreateDirectory(ResultRegistryPath.resultIndexRoot executionStoreRoot) |> ignore

    let fileGate (path: string) = fileGates.GetOrAdd(path, fun _ -> obj ())

    let appendRecord (record: FsiExecutionRecord) =
        ensureDirectories ()
        let path = ResultRegistryPath.agentResultPath executionStoreRoot record.AgentId
        let line = FSharpJson.serialize record + Environment.NewLine

        lock (fileGate path) (fun () ->
            use stream = File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
            use writer = new StreamWriter(stream, Encoding.UTF8)
            writer.Write(line))

    let readPersistedRecords (path: string) =
        lock (fileGate path) (fun () ->
            if File.Exists(path) then
                use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                use reader = new StreamReader(stream, Encoding.UTF8)
                let lines = ResizeArray<string>()

                while not reader.EndOfStream do
                    lines.Add(reader.ReadLine())

                lines
                |> Seq.filter (fun line -> not (String.IsNullOrWhiteSpace(line)))
                |> Seq.map PersistedExecutionRecord.deserialize
                |> Seq.toList
            else
                [])

    let loadPersistedRecords () =
        ensureDirectories ()

        Directory.EnumerateFiles(ResultRegistryPath.resultIndexRoot executionStoreRoot, "*.jsonl")
        |> Seq.collect readPersistedRecords
        |> Seq.iter (fun record -> results.[record.ResultId] <- record)

    do loadPersistedRecords ()

    member _.ExecutionStoreRoot = executionStoreRoot

    interface IExecutionStore with
        member _.Put(record: FsiExecutionRecord) =
            results.[record.ResultId] <- record
            appendRecord record

        member _.TryGet(resultId: string) =
            match results.TryGetValue resultId with
            | true, record -> Some record
            | false, _ -> None

        member _.ListBySession(route: ExecutionRoute) =
            results.Values
            |> Seq.filter (fun record ->
                record.AgentId = route.AgentId
                && record.HostId = route.HostId
                && record.SessionId = route.SessionId)
            |> Seq.sortByDescending (fun record -> record.SubmittedAt)
            |> Seq.toList

        member _.ListBySessionId(sessionId: string) =
            results.Values
            |> Seq.filter (fun record -> record.SessionId = sessionId)
            |> Seq.sortByDescending (fun record -> record.SubmittedAt)
            |> Seq.toList

        member _.ListByAgent(agentId: string) =
            results.Values
            |> Seq.filter (fun record -> record.AgentId = agentId)
            |> Seq.sortByDescending (fun record -> record.SubmittedAt)
            |> Seq.toList

        member _.List(?agentId: string, ?hostId: string, ?sessionId: string, ?metadata: (string * string) list, ?limit: int) =
            ExecutionStoreQuery.list agentId hostId sessionId metadata limit results.Values
