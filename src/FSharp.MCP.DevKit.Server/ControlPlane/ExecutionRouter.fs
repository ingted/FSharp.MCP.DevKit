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

    member this.RouteAndExecute(request: ExecutionRequest) : Task<FsiExecutionRecord> =
        task {
            let host =
                hostRegistry.TryGet request.Route.HostId
                |> Option.defaultWith (fun () -> invalidOp $"Host '{request.Route.HostId}' was not found.")

            let backend = backendSelector.Resolve(host.BackendKind)
            let! record = backend.Execute request
            resultRegistry.Put record

            let! backendSession = backend.GetSessionState(request.Route)

            let updatedSession =
                { backendSession with
                    Status =
                        if record.Result.IsSuccess then
                            backendSession.Status
                        else
                            SessionFaulted
                    LastExecutionAt = record.CompletedAt |> Option.orElse backendSession.LastExecutionAt }

            this.UpdateSessionRecord updatedSession

            agentRegistry.Touch request.Route.AgentId
            return record
        }
