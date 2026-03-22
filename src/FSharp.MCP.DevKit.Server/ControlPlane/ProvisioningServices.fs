namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.Backends
open FSharp.MCP.DevKit.Server.Integration

type HostProvisioningService
    (
        agentRegistry: IAgentRegistry,
        hostRegistry: IHostRegistry,
        procSupervisorClient: IProcSupervisorClient
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
                let! snapshot = procSupervisorClient.StartProc(hostId, spec)

                let readyRecord =
                    { creatingRecord with
                        Status = mapHostStatus snapshot.Status
                        Address = snapshot.FsiSupervisorPath |> Option.orElse snapshot.NodeAddress
                        ProcId = snapshot.ProcessId
                        LastHealthCheckAt = Some DateTime.UtcNow
                        LastError = snapshot.LastError }

                hostRegistry.Update readyRecord
                return readyRecord
            with ex ->
                let failedRecord =
                    { creatingRecord with
                        Status = Faulted
                        LastHealthCheckAt = Some DateTime.UtcNow
                        LastError = Some ex.Message }

                hostRegistry.Update failedRecord
                return raise ex
        }

type SessionProvisioningService
    (
        hostRegistry: IHostRegistry,
        sessionRegistry: ISessionRegistry,
        backendSelector: BackendSelector
    ) =

    let upsertSession (record: SessionRecord) =
        match sessionRegistry.TryGet(record.HostId, record.SessionId) with
        | Some _ -> sessionRegistry.Update record
        | None -> sessionRegistry.Create record |> ignore

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
                let! initialState = backend.GetSessionState(route)

                let! hydratedState =
                    if initialState.Status = SessionMissing then
                        let bootstrapRequest =
                            { RequestId = Guid.NewGuid().ToString("N")
                              Route = route
                              OperationKind = ExecuteCode
                              Payload = "()"
                              Timeout = Some(TimeSpan.FromSeconds 30.0)
                              UsePackageTargets = None }

                        task {
                            let! _ = backend.Execute(bootstrapRequest)
                            return! backend.GetSessionState(route)
                        }
                    else
                        Task.FromResult initialState

                let record =
                    { hydratedState with
                        SessionName = defaultArg sessionName hydratedState.SessionName }

                upsertSession record
                return record
        }
