namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Collections.Concurrent
open FSharp.MCP.DevKit.Core

type InMemoryAgentRegistry() =
    let agents = ConcurrentDictionary<string, AgentRecord>()

    interface IAgentRegistry with
        member _.Register(record: AgentRecord) =
            agents.[record.AgentId] <- record
            record

        member _.TryGet(agentId: string) =
            match agents.TryGetValue agentId with
            | true, record -> Some record
            | false, _ -> None

        member _.Touch(agentId: string) =
            agents.AddOrUpdate(
                agentId,
                (fun key ->
                    { AgentId = key
                      DisplayName = None
                      CreatedAt = DateTime.UtcNow
                      LastSeenAt = DateTime.UtcNow
                      DefaultHostId = None
                      Metadata = Map.empty }),
                (fun _ existing ->
                    { existing with
                        LastSeenAt = DateTime.UtcNow })
            )
            |> ignore

        member _.List() = agents.Values |> Seq.toList
