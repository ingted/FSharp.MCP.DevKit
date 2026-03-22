namespace FSharp.MCP.DevKit.Server.ControlPlane

open System.Collections.Concurrent
open FSharp.MCP.DevKit.Core

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

        member _.ListByAgent(agentId: string) =
            results.Values
            |> Seq.filter (fun record -> record.AgentId = agentId)
            |> Seq.sortByDescending (fun record -> record.SubmittedAt)
            |> Seq.toList
