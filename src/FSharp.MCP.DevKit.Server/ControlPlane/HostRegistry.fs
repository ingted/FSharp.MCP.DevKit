namespace FSharp.MCP.DevKit.Server.ControlPlane

open System.Collections.Concurrent
open FSharp.MCP.DevKit.Core

type InMemoryHostRegistry() =
    let hosts = ConcurrentDictionary<string, HostRecord>()

    interface IHostRegistry with
        member _.Create(record: HostRecord) =
            hosts.[record.HostId] <- record
            record

        member _.Update(record: HostRecord) = hosts.[record.HostId] <- record

        member _.TryGet(hostId: string) =
            match hosts.TryGetValue hostId with
            | true, record -> Some record
            | false, _ -> None

        member _.ListByAgent(agentId: string) =
            hosts.Values
            |> Seq.filter (fun record -> record.AgentId = agentId)
            |> Seq.toList
