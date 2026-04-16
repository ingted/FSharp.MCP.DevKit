namespace FSharp.MCP.DevKit.Server.Backends

open System
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Messages

type NetFxHostBackend(remoteClient: IRemoteFsiClient) =
    let mapStatus (value: string) =
        match value with
        | "SessionBusy" -> SessionBusy
        | "SessionFaulted" -> SessionFaulted
        | "SessionMissing" -> SessionMissing
        | _ -> SessionReady

    let toSessionRecord (route: ExecutionRoute) (state: FsiRemoteSessionState option) =
        match state with
        | Some snapshot ->
            { SessionId = snapshot.SessionId
              AgentId = route.AgentId
              HostId = route.HostId
              SessionName = snapshot.SessionName
              Status = mapStatus snapshot.Status
              Refs = snapshot.Refs
              Loads = snapshot.Loads
              SearchPaths = snapshot.SearchPaths
              Variables = snapshot.Variables
              LastCheckpointId = snapshot.LastCheckpointId
              RunningSinceUtc = snapshot.RunningSinceUtc
              LastExecutionAt = snapshot.LastExecutionAt }
        | None ->
            { SessionId = route.SessionId
              AgentId = route.AgentId
              HostId = route.HostId
              SessionName = route.SessionId
              Status = SessionMissing
              Refs = []
              Loads = []
              SearchPaths = []
              Variables = []
              LastCheckpointId = None
              RunningSinceUtc = None
              LastExecutionAt = None }

    let send (commandType: string) (payload: string) (route: ExecutionRoute option) (usePackageTargets: bool option) (timeout: TimeSpan option) =
        remoteClient.SendCommand(
            { CommandType = commandType
              Payload = payload
              Route = route
              UsePackageTargets = usePackageTargets
              Timeout = timeout }
        )

    let commandFor operationKind =
        match operationKind with
        | ExecuteCode -> "EXEC"
        | EvaluateExpression -> "EVAL"
        | LoadScript -> "LOAD"
        | ReferenceAssembly -> "REFERENCE_ASSEMBLY"
        | ReferenceNuget -> "REFERENCE_NUGET"
        | AddSearchPath -> "ADD_PATH"
        | ResetSession -> "RESET"
        | RestartHost -> "RESTART_HOST"
        | GetState -> "STATE"
        | ResultQuery -> "RESULT_OP"

    interface IFsiExecutionBackend with
        member _.BackendKind = NetFxRemote

        member _.Execute(request: ExecutionRequest) =
            task {
                let submittedAt = DateTime.UtcNow
                let startedAt = Some DateTime.UtcNow
                let! response =
                    send
                        (commandFor request.OperationKind)
                        request.Payload
                        (Some request.Route)
                        request.UsePackageTargets
                        request.Timeout

                let result = response.Result
                let completedAt = DateTime.UtcNow

                return
                    BackendAdapters.toExecutionRecord
                        NetFxRemote
                        request
                        submittedAt
                        startedAt
                        (Some completedAt)
                        (response.HostId |> Option.defaultValue request.Route.HostId)
                        (response.SessionId |> Option.defaultValue request.Route.SessionId)
                        (Guid.NewGuid().ToString("N"))
                        { Output = result.Output
                          Errors = result.Errors
                          IsSuccess = result.IsSuccess
                          ExecutionTime = result.ExecutionTimeMs |> Option.map TimeSpan.FromMilliseconds
                          Diagnostics =
                              result.Diagnostics
                              |> Array.map (fun diagnostic ->
                                  { FileName = diagnostic.FileName
                                    StartLine = diagnostic.StartLine
                                    EndLine = diagnostic.EndLine
                                    StartColumn = diagnostic.StartColumn
                                    EndColumn = diagnostic.EndColumn
                                    Severity = diagnostic.Severity
                                    Message = diagnostic.Message })
                          Value = result.Value }
                        result.RawErrorType
            }

        member _.GetSessionState(route: ExecutionRoute) =
            task {
                let! response = send "STATE" "" (Some route) None (Some(TimeSpan.FromSeconds 30.0))
                return toSessionRecord route response.SessionState
            }

        member this.EnsureSession(route: ExecutionRoute) =
            task {
                let! state = (this :> IFsiExecutionBackend).GetSessionState(route)

                if state.Status <> SessionMissing then
                    return state
                else
                    return
                        { state with
                            Status = SessionReady
                            RunningSinceUtc = Some DateTime.UtcNow }
            }

        member this.ResetSession(route: ExecutionRoute) =
            task {
                let request =
                    { RequestId = Guid.NewGuid().ToString("N")
                      Route = route
                      OperationKind = ResetSession
                      Payload = ""
                      Timeout = Some(TimeSpan.FromSeconds 30.0)
                      UsePackageTargets = None
                      Metadata = Map.empty }

                return! (this :> IFsiExecutionBackend).Execute request
            }

        member _.RestartHost(host: HostRecord) =
            task {
                let hostRoute : ExecutionRoute =
                    { AgentId = host.AgentId
                      HostId = host.HostId
                      SessionId = "host-control" }

                let! _ = send "RESTART_HOST" "" (Some hostRoute) None (Some(TimeSpan.FromSeconds 30.0))
                return ()
            }

        member _.HealthCheck(host: HostRecord) =
            task {
                let route : ExecutionRoute =
                    { AgentId = host.AgentId
                      HostId = host.HostId
                      SessionId = "host-control" }

                try
                    let! response = send "PING" "" (Some route) None (Some(TimeSpan.FromSeconds 5.0))

                    return
                        { BackendKind = NetFxRemote
                          IsAvailable = response.Result.IsSuccess
                          Message = Some response.Result.Output
                          HostId = Some host.HostId
                          CheckedAt = DateTime.UtcNow }
                with ex ->
                    return
                        { BackendKind = NetFxRemote
                          IsAvailable = false
                          Message = Some ex.Message
                          HostId = Some host.HostId
                          CheckedAt = DateTime.UtcNow }
            }
