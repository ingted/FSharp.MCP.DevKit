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

type InMemoryResultRegistry() =
    let results = ConcurrentDictionary<string, FsiExecutionRecord>()

    interface IResultRegistry with
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

type JsonLineResultRegistry(?executionStoreRoot: string) =
    let executionStoreRoot =
        executionStoreRoot
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace(value)))
        |> Option.defaultWith SessionOutputArchivePath.resolveExecutionStoreRoot

    let results = ConcurrentDictionary<string, FsiExecutionRecord>()
    let fileGates = ConcurrentDictionary<string, obj>()

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

    let loadPersistedRecords () =
        ensureDirectories ()

        Directory.EnumerateFiles(ResultRegistryPath.resultIndexRoot executionStoreRoot, "*.jsonl")
        |> Seq.collect (fun path ->
            File.ReadLines(path)
            |> Seq.filter (fun line -> not (String.IsNullOrWhiteSpace(line)))
            |> Seq.map FSharpJson.deserialize<FsiExecutionRecord>)
        |> Seq.iter (fun record -> results.[record.ResultId] <- record)

    do loadPersistedRecords ()

    member _.ExecutionStoreRoot = executionStoreRoot

    interface IResultRegistry with
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
