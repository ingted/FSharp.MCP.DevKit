namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.Backends

type ExecutionRouter
    (
        agentRegistry: IAgentRegistry,
        hostRegistry: IHostRegistry,
        sessionRegistry: ISessionRegistry,
        resultRegistry: IResultRegistry,
        backendSelector: BackendSelector
    ) =

    member _.ResolveRoute(requestedRoute: ExecutionRoute option) =
        DefaultRouting.resolve agentRegistry hostRegistry sessionRegistry requestedRoute

    member private _.UpdateSessionRecord(record: SessionRecord) =
        match sessionRegistry.TryGet(record.HostId, record.SessionId) with
        | Some _ -> sessionRegistry.Update record
        | None -> sessionRegistry.Create record |> ignore

    member private _.CreateFaultedSessionRecord(request: ExecutionRequest, host: HostRecord, previousFailedResultId: string option) =
        let now = DateTime.UtcNow

        let previousFailureHint =
            previousFailedResultId
            |> Option.map (fun resultId -> $"\nPreviousFailedResultId: {resultId}")
            |> Option.defaultValue ""

        BackendAdapters.toExecutionRecord
            host.BackendKind
            request
            now
            (Some now)
            (Some now)
            host.HostId
            request.Route.SessionId
            (Guid.NewGuid().ToString("N"))
            (BackendAdapters.createFailedResult
                ($"Session '{request.Route.SessionId}' on host '{request.Route.HostId}' is in Faulted state due to an earlier execution failure. Call reset_fsi_session_routed or create_fsi_session to recover.{previousFailureHint}")
                None
                (Some "SessionFaulted"))
            (Some "SessionFaulted")

    member this.RouteAndExecute(request: ExecutionRequest) : Task<FsiExecutionRecord> =
        task {
            let host =
                hostRegistry.TryGet request.Route.HostId
                |> Option.defaultWith (fun () -> invalidOp $"Host '{request.Route.HostId}' was not found. Use list_fsi_hosts to see available hosts.")

            let shouldBlockOnFaultedState =
                match request.OperationKind with
                | ExecuteCode
                | EvaluateExpression
                | LoadScript
                | ReferenceAssembly
                | ReferenceNuget
                | AddSearchPath -> true
                | GetState
                | ResetSession
                | RestartHost
                | ResultQuery -> false

            match sessionRegistry.TryGet(request.Route.HostId, request.Route.SessionId) with
            | Some existingSession when shouldBlockOnFaultedState && existingSession.Status = SessionFaulted ->
                let previousFailedResultId =
                    resultRegistry.ListBySession request.Route
                    |> List.tryFind (fun record -> not record.Result.IsSuccess)
                    |> Option.map (fun record -> record.ResultId)

                let record = this.CreateFaultedSessionRecord(request, host, previousFailedResultId)
                resultRegistry.Put record
                agentRegistry.Touch request.Route.AgentId
                return record
            | _ ->
                let backend = backendSelector.Resolve(host.BackendKind)
                let! record = backend.Execute request
                resultRegistry.Put record

                let! backendSession = backend.GetSessionState(request.Route)

                let updatedSession =
                    { backendSession with
                        Status = backendSession.Status
                        LastExecutionAt = record.CompletedAt |> Option.orElse backendSession.LastExecutionAt }

                this.UpdateSessionRecord updatedSession

                agentRegistry.Touch request.Route.AgentId
                return record
        }
