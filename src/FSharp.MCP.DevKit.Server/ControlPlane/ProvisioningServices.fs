namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Threading.Tasks
open Akka.Actor
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.Backends
open FSharp.MCP.DevKit.Server.Integration

type HostProvisioningService
    (
        agentRegistry: IAgentRegistry,
        hostRegistry: IHostRegistry,
        procSupervisorClient: IProcSupervisorClient,
        inventoryEventStore: IInventoryEventStore
    ) =

    let mapBackendKind hostKind =
        match hostKind with
        | NetFxHost -> NetFxRemote
        | Net10Host -> Net10Remote
        | InProcHost -> invalidOp "Explicit host creation does not support InProcHost."

    let mapHostStatus (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "starting" -> Creating
        | "running" -> Ready
        | "stopped" -> Stopped
        | "failed" -> Faulted
        | _ -> Degraded

    let ensureAgent agentId hostId =
        let now = DateTime.UtcNow

        match agentRegistry.TryGet agentId with
        | Some existing ->
            let updated =
                { existing with
                    LastSeenAt = now
                    DefaultHostId = existing.DefaultHostId |> Option.orElse (Some hostId) }

            agentRegistry.Register updated |> ignore
        | None ->
            agentRegistry.Register(
                { AgentId = agentId
                  DisplayName = None
                  CreatedAt = now
                  LastSeenAt = now
                  DefaultHostId = Some hostId
                  Metadata = Map.empty }
            )
            |> ignore

    let tryGetProcSnapshot
        (procSupervisorClient: IProcSupervisorClient)
        (hostId: string)
        : Task<ProcHostSnapshot option> =
        task {
            try
                let! direct = procSupervisorClient.GetProcInfo(hostId)

                match direct with
                | Some snapshot -> return Some snapshot
                | None ->
                    let! snapshots = procSupervisorClient.ListProcInfo()
                    return snapshots |> List.tryFind (fun snapshot -> snapshot.ProcId = hostId)
            with :? AskTimeoutException ->
                let! snapshots = procSupervisorClient.ListProcInfo()
                return snapshots |> List.tryFind (fun snapshot -> snapshot.ProcId = hostId)
        }

    let rec pollProcSnapshot
        (procSupervisorClient: IProcSupervisorClient)
        (hostId: string)
        (deadlineUtc: DateTime)
        (isAcceptable: ProcHostSnapshot -> bool)
        (remainingAttempts: int)
        : Task<ProcHostSnapshot option> =
        task {
            if DateTime.UtcNow >= deadlineUtc || remainingAttempts <= 0 then
                return None
            else
                let! snapshotOpt = tryGetProcSnapshot procSupervisorClient hostId

                match snapshotOpt with
                | Some snapshot when not (String.IsNullOrWhiteSpace snapshot.Status) && isAcceptable snapshot ->
                    return Some snapshot
                | _ ->
                    do! Task.Delay 250
                    return! pollProcSnapshot procSupervisorClient hostId deadlineUtc isAcceptable (remainingAttempts - 1)
        }

    let snapshotHasUsableRemoteAddress hostKind (snapshot: ProcHostSnapshot) =
        match hostKind with
        | Net10Host
        | NetFxHost -> snapshot.FsiSupervisorPath |> Option.exists (String.IsNullOrWhiteSpace >> not)
        | InProcHost -> true

    let appendHostEvent eventKind message (record: HostRecord) =
        inventoryEventStore.Append
            { SequenceId = 0L
              EventKind = eventKind
              SubjectKind = "host"
              AgentId = Some record.AgentId
              HostId = Some record.HostId
              SessionId = None
              CreatedAt = DateTime.UtcNow
              Message = message }
        |> ignore

    member _.CreateHost
        (
            agentId: string,
            hostKind: HostKind,
            spec: ProcHostSpec,
            ?requestedHostId: string
        ) : Task<HostRecord> =
        task {
            if hostKind = InProcHost then
                invalidOp "Explicit host creation does not support InProcHost."

            let hostId = defaultArg requestedHostId (Guid.NewGuid().ToString("N"))

            match hostRegistry.TryGet hostId with
            | Some _ -> invalidOp $"Host '{hostId}' already exists."
            | None -> ()

            ensureAgent agentId hostId

            let now = DateTime.UtcNow

            let creatingRecord =
                { HostId = hostId
                  AgentId = agentId
                  HostKind = hostKind
                  BackendKind = mapBackendKind hostKind
                  Status = Creating
                  Address = None
                  ProcId = None
                  CreatedAt = now
                  LastHealthCheckAt = None
                  LastError = None }

            hostRegistry.Create creatingRecord |> ignore

            try
                let! snapshot =
                    task {
                        try
                            return! procSupervisorClient.StartProc(hostId, spec)
                        with :? AskTimeoutException ->
                            let! recovered =
                                pollProcSnapshot
                                    procSupervisorClient
                                    hostId
                                    (DateTime.UtcNow.AddSeconds 15.0)
                                    (fun snapshot -> not (String.IsNullOrWhiteSpace snapshot.Status))
                                    60

                            match recovered with
                            | Some snapshot -> return snapshot
                            | None ->
                                return raise (AskTimeoutException("Timed out waiting for ProcSupervisor StartProc response and no proc snapshot became visible during recovery polling."))
                    }

                let! finalizedSnapshot =
                    if snapshotHasUsableRemoteAddress hostKind snapshot then
                        Task.FromResult snapshot
                    else
                        task {
                            let! recovered =
                                pollProcSnapshot
                                    procSupervisorClient
                                    hostId
                                    (DateTime.UtcNow.AddSeconds 15.0)
                                    (snapshotHasUsableRemoteAddress hostKind)
                                    60

                            return recovered |> Option.defaultValue snapshot
                        }

                let readyRecord =
                    { creatingRecord with
                        Status = mapHostStatus finalizedSnapshot.Status
                        Address = finalizedSnapshot.FsiSupervisorPath |> Option.orElse finalizedSnapshot.NodeAddress
                        ProcId = finalizedSnapshot.ProcessId
                        LastHealthCheckAt = Some DateTime.UtcNow
                        LastError = finalizedSnapshot.LastError }

                hostRegistry.Update readyRecord
                appendHostEvent "host.upserted" (Some $"Host '{readyRecord.HostId}' status = {readyRecord.Status}") readyRecord
                return readyRecord
            with ex ->
                let failedRecord =
                    { creatingRecord with
                        Status = Faulted
                        LastHealthCheckAt = Some DateTime.UtcNow
                        LastError = Some ex.Message }

                hostRegistry.Update failedRecord
                appendHostEvent "host.upserted" (Some ex.Message) failedRecord
                return raise ex
        }

type SessionProvisioningService
    (
        hostRegistry: IHostRegistry,
        sessionRegistry: ISessionRegistry,
        backendSelector: BackendSelector,
        inventoryEventStore: IInventoryEventStore
    ) =

    let upsertSession (record: SessionRecord) =
        match sessionRegistry.TryGet(record.HostId, record.SessionId) with
        | Some _ -> sessionRegistry.Update record
        | None -> sessionRegistry.Create record |> ignore

    let appendSessionEvent eventKind message (record: SessionRecord) =
        inventoryEventStore.Append
            { SequenceId = 0L
              EventKind = eventKind
              SubjectKind = "session"
              AgentId = Some record.AgentId
              HostId = Some record.HostId
              SessionId = Some record.SessionId
              CreatedAt = DateTime.UtcNow
              Message = message }
        |> ignore

    member _.CreateSession
        (
            agentId: string,
            hostId: string,
            ?sessionId: string,
            ?sessionName: string
        ) : Task<SessionRecord> =
        task {
            let host =
                hostRegistry.TryGet hostId
                |> Option.defaultWith (fun () -> invalidOp $"Host '{hostId}' was not found.")

            if host.AgentId <> agentId then
                invalidOp $"Host '{hostId}' does not belong to agent '{agentId}'."

            let resolvedSessionId = defaultArg sessionId (Guid.NewGuid().ToString("N"))

            match sessionRegistry.TryGet(hostId, resolvedSessionId) with
            | Some existing -> return existing
            | None ->
                let route =
                    { AgentId = agentId
                      HostId = hostId
                      SessionId = resolvedSessionId }

                let backend = backendSelector.Resolve(host.BackendKind)
                let! ensuredState = backend.EnsureSession(route)

                let record =
                    { ensuredState with
                        AgentId = agentId
                        HostId = hostId
                        SessionId = resolvedSessionId
                        SessionName =
                            let preferredName = defaultArg sessionName ensuredState.SessionName
                            if String.IsNullOrWhiteSpace preferredName then
                                resolvedSessionId
                            else
                                preferredName }

                upsertSession record
                appendSessionEvent "session.upserted" (Some $"Session '{record.SessionId}' status = {record.Status}") record
                return record
        }
