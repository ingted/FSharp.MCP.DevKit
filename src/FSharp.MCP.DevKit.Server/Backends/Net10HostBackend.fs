namespace FSharp.MCP.DevKit.Server.Backends

open System
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.Integration

type Net10HostBackend
    (
        hostRegistry: IHostRegistry,
        fsiSupervisorClient: IFsiSupervisorClient,
        procSupervisorClient: IProcSupervisorClient
    ) =

    let mapSessionStatus (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "busy" -> SessionBusy
        | "faulted" -> SessionFaulted
        | "missing" -> SessionMissing
        | _ -> SessionReady

    let mapProcStatus (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "starting" -> Creating
        | "running" -> Ready
        | "stopped" -> Stopped
        | "failed" -> Faulted
        | _ -> Degraded

    let requireHost (hostId: string) =
        hostRegistry.TryGet hostId
        |> Option.defaultWith (fun () -> invalidOp $"Host '{hostId}' was not found.")

    let toSessionRecord (route: ExecutionRoute) (snapshot: FsiSupervisorSessionSnapshot) =
        { SessionId = snapshot.SessionId
          AgentId = route.AgentId
          HostId = route.HostId
          SessionName = snapshot.SessionId
          Status = mapSessionStatus snapshot.Status
          Refs = snapshot.Refs
          Loads = snapshot.Loads
          SearchPaths = snapshot.SearchPaths
          Variables = snapshot.Variables
          LastCheckpointId = snapshot.LastCheckpointId
          RunningSinceUtc = snapshot.RunningSinceUtc
          LastExecutionAt = None }

    let buildExecRequest (request: ExecutionRequest) =
        match request.OperationKind with
        | ExecuteCode
        | EvaluateExpression ->
            Ok
                { RequestId = request.RequestId
                  SessionId = request.Route.SessionId
                  Code = request.Payload
                  Refs = []
                  Loads = []
                  Timeout = request.Timeout
                  CaptureStdout = Some true }
        | LoadScript ->
            Ok
                { RequestId = request.RequestId
                  SessionId = request.Route.SessionId
                  Code = ""
                  Refs = []
                  Loads = [ request.Payload ]
                  Timeout = request.Timeout
                  CaptureStdout = Some true }
        | ReferenceAssembly ->
            Ok
                { RequestId = request.RequestId
                  SessionId = request.Route.SessionId
                  Code = ""
                  Refs = [ request.Payload ]
                  Loads = []
                  Timeout = request.Timeout
                  CaptureStdout = Some true }
        | ReferenceNuget ->
            Ok
                { RequestId = request.RequestId
                  SessionId = request.Route.SessionId
                  Code = sprintf "#r %A" $"nuget: {request.Payload}"
                  Refs = []
                  Loads = []
                  Timeout = request.Timeout
                  CaptureStdout = Some true }
        | AddSearchPath ->
            Ok
                { RequestId = request.RequestId
                  SessionId = request.Route.SessionId
                  Code = sprintf "#I %A" request.Payload
                  Refs = []
                  Loads = []
                  Timeout = request.Timeout
                  CaptureStdout = Some true }
        | GetState -> Error "GetState is handled via GetSessionState."
        | ResetSession -> Error "ResetSession is not implemented for Net10HostBackend yet."
        | RestartHost -> Error "RestartHost is handled via ProcSupervisor."
        | ResultQuery -> Error "ResultQuery is not implemented for Net10HostBackend yet."

    let createUnsupportedRecord (request: ExecutionRequest) (host: HostRecord) (message: string) =
        let now = DateTime.UtcNow
        BackendAdapters.toExecutionRecord
            Net10Remote
            request
            now
            (Some now)
            (Some now)
            host.HostId
            request.Route.SessionId
            (Guid.NewGuid().ToString("N"))
            (BackendAdapters.createFailedResult message None (Some "UnsupportedOperationException"))
            (Some "UnsupportedOperationException")

    interface IFsiExecutionBackend with
        member _.BackendKind = Net10Remote

        member _.Execute(request: ExecutionRequest) =
            task {
                let host = requireHost request.Route.HostId

                match buildExecRequest request with
                | Error message -> return createUnsupportedRecord request host message
                | Ok execRequest ->
                    let submittedAt = DateTime.UtcNow
                    let startedAt = Some DateTime.UtcNow
                    let! result = fsiSupervisorClient.Execute(host, execRequest)
                    let completedAt = DateTime.UtcNow

                    return
                        BackendAdapters.toExecutionRecord
                            Net10Remote
                            request
                            submittedAt
                            startedAt
                            (Some completedAt)
                            host.HostId
                            result.SessionId
                            (Guid.NewGuid().ToString("N"))
                            result.Result
                            result.RawErrorType
            }

        member _.GetSessionState(route: ExecutionRoute) =
            task {
                let host = requireHost route.HostId
                let! snapshot = fsiSupervisorClient.GetSessionInfo(host, route.SessionId)
                return toSessionRecord route snapshot
            }

        member _.ResetSession(route: ExecutionRoute) =
            task {
                let host = requireHost route.HostId
                let submittedAt = DateTime.UtcNow
                let request =
                    { RequestId = Guid.NewGuid().ToString("N")
                      Route = route
                      OperationKind = ResetSession
                      Payload = ""
                      Timeout = Some(TimeSpan.FromSeconds 30.0)
                      UsePackageTargets = None }
                let! reset = fsiSupervisorClient.ResetSession(host, route.SessionId)
                let completedAt = DateTime.UtcNow

                let result =
                    { Output = "FSI session reset"
                      Errors = ""
                      IsSuccess = true
                      ExecutionTime = Some(completedAt - submittedAt)
                      Diagnostics = [||]
                      Value = Some reset.Status }

                return
                    BackendAdapters.toExecutionRecord
                        Net10Remote
                        request
                        submittedAt
                        (Some submittedAt)
                        (Some completedAt)
                        host.HostId
                        reset.SessionId
                        (Guid.NewGuid().ToString("N"))
                        result
                        None
            }

        member _.RestartHost(host: HostRecord) =
            task {
                let! _ = procSupervisorClient.RestartProc(host.HostId)
                return ()
            }

        member _.HealthCheck(host: HostRecord) =
            task {
                let! snapshot = procSupervisorClient.GetProcInfo(host.HostId)

                match snapshot with
                | Some value ->
                    let hostStatus = mapProcStatus value.Status
                    let available = hostStatus = Ready && value.LastProbeOk <> Some false
                    let message = value.LastError |> Option.orElse (Some value.Status)

                    return
                        { BackendKind = Net10Remote
                          IsAvailable = available
                          Message = message
                          HostId = Some host.HostId
                          CheckedAt = DateTime.UtcNow }
                | None ->
                    return
                        { BackendKind = Net10Remote
                          IsAvailable = false
                          Message = Some "ProcSupervisor snapshot not found"
                          HostId = Some host.HostId
                          CheckedAt = DateTime.UtcNow }
            }
