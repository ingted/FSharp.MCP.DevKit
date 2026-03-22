namespace FSharp.MCP.DevKit.Server.ControlPlane

open System.Collections.Concurrent
open FSharp.MCP.DevKit.Core

type InMemorySessionRegistry() =
    let sessions = ConcurrentDictionary<string, SessionRecord>()

    let keyOf (hostId: string) (sessionId: string) = $"{hostId}::{sessionId}"

    interface ISessionRegistry with
        member _.Create(record: SessionRecord) =
            sessions.[keyOf record.HostId record.SessionId] <- record
            record

        member _.Update(record: SessionRecord) =
            sessions.[keyOf record.HostId record.SessionId] <- record

        member _.TryGet(hostId: string, sessionId: string) =
            match sessions.TryGetValue(keyOf hostId sessionId) with
            | true, record -> Some record
            | false, _ -> None

        member _.ListByHost(hostId: string) =
            sessions.Values
            |> Seq.filter (fun record -> record.HostId = hostId)
            |> Seq.toList
