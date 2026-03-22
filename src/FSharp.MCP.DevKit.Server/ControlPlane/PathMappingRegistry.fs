namespace FSharp.MCP.DevKit.Server.ControlPlane

open System.Collections.Concurrent

type InMemoryPathMappingRegistry() =
    let mappings = ConcurrentDictionary<string, PathMappingRecord>()

    interface IPathMappingRegistry with
        member _.Put(record: PathMappingRecord) = mappings.[record.MappingId] <- record

        member _.List() =
            mappings.Values
            |> Seq.sortByDescending (fun record -> record.CreatedAt)
            |> Seq.toList

        member _.ListByAgent(agentId: string) =
            mappings.Values
            |> Seq.filter (fun record -> record.AgentId = Some agentId)
            |> Seq.sortByDescending (fun record -> record.CreatedAt)
            |> Seq.toList

        member _.ListByHost(hostId: string) =
            mappings.Values
            |> Seq.filter (fun record -> record.HostId = Some hostId)
            |> Seq.sortByDescending (fun record -> record.CreatedAt)
            |> Seq.toList
