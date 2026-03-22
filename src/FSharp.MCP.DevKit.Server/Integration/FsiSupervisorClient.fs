namespace FSharp.MCP.DevKit.Server.Integration

open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSI.Contracts
open FSharp.MCP.DevKit.Core

type FsiSupervisorExecRequest =
    { RequestId: string
      SessionId: string
      Code: string
      Refs: string list
      Loads: string list
      Timeout: TimeSpan option
      CaptureStdout: bool option }

type FsiSupervisorExecutionResult =
    { SessionId: string
      Result: FsiResult
      RawErrorType: string option }

type FsiSupervisorSessionSnapshot =
    { SessionId: string
      Status: string
      Refs: string list
      Loads: string list
      SearchPaths: string list
      Variables: (string * string) list
      LastCheckpointId: string option
      RunningSinceUtc: DateTime option }

type FsiSupervisorResetResult =
    { SessionId: string
      Existed: bool
      Status: string }

type IFsiSupervisorClient =
    abstract member Execute: host: HostRecord * request: FsiSupervisorExecRequest -> Task<FsiSupervisorExecutionResult>
    abstract member GetSessionInfo: host: HostRecord * sessionId: string -> Task<FsiSupervisorSessionSnapshot>
    abstract member ListSessions: host: HostRecord -> Task<FsiSupervisorSessionSnapshot list>
    abstract member ResetSession: host: HostRecord * sessionId: string -> Task<FsiSupervisorResetResult>

module private FsiSupervisorAdapters =
    let private combineErrors (stderr: string) (error: ErrorInfo option) =
        [ if not (String.IsNullOrWhiteSpace stderr) then
              stderr

          match error with
          | Some value when not (String.IsNullOrWhiteSpace value.message) -> value.message
          | _ -> () ]
        |> String.concat Environment.NewLine

    let private toDiagnostic (diagnostic: DiagnosticInfo) : FsiDiagnostic =
        let range = diagnostic.range

        { FileName = range |> Option.map (fun value -> value.file) |> Option.defaultValue ""
          StartLine = range |> Option.map (fun value -> value.startLine) |> Option.defaultValue 0
          EndLine = range |> Option.map (fun value -> value.endLine) |> Option.defaultValue 0
          StartColumn = range |> Option.map (fun value -> value.startCol) |> Option.defaultValue 0
          EndColumn = range |> Option.map (fun value -> value.endCol) |> Option.defaultValue 0
          Severity = diagnostic.severity
          Message = diagnostic.message }

    let toResult (result: ExecResult) : FsiSupervisorExecutionResult =
        { SessionId = result.session
          RawErrorType = result.error |> Option.map (fun value -> value.code)
          Result =
            { Output = result.stdout
              Errors = combineErrors result.stderr result.error
              IsSuccess = result.ok
              ExecutionTime = Some(TimeSpan.FromMilliseconds(float result.elapsedMs))
              Diagnostics = result.diagnostics |> List.toArray |> Array.map toDiagnostic
              Value = result.resultJson } }

    let toSessionSnapshot (session: SessionInfo) : FsiSupervisorSessionSnapshot =
        { SessionId = session.session
          Status = session.status
          Refs = session.refs
          Loads = session.loads
          SearchPaths = session.searchPaths
          Variables = session.variables
          LastCheckpointId = session.lastCheckpointId
          RunningSinceUtc = session.runningSinceUtc }

    let toResetResult (result: ResetSessionResult) : FsiSupervisorResetResult =
        { SessionId = result.session
          Existed = result.existed
          Status = result.status }

type FsiSupervisorClient(actorSystem: ActorSystem, ?defaultTimeout: TimeSpan) =
    let defaultTimeout = defaultArg defaultTimeout (TimeSpan.FromSeconds 30.0)

    let resolveSupervisor (host: HostRecord) =
        task {
            let path =
                host.Address
                |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                |> Option.defaultWith (fun () -> invalidOp $"Host '{host.HostId}' does not have a supervisor address.")

            let selection = actorSystem.ActorSelection(path)
            return! selection.ResolveOne(defaultTimeout)
        }

    interface IFsiSupervisorClient with
        member _.Execute(host: HostRecord, request: FsiSupervisorExecRequest) =
            task {
                let timeout = defaultArg request.Timeout defaultTimeout
                let! supervisor = resolveSupervisor host

                let execRequest: ExecCode =
                    { id = request.RequestId
                      session = request.SessionId
                      code = request.Code
                      refs = request.Refs
                      loads = request.Loads
                      args = None
                      timeoutMs = Some(int timeout.TotalMilliseconds)
                      captureStdout = request.CaptureStdout }

                let! result = supervisor.Ask<ExecResult>(execRequest, timeout)
                return FsiSupervisorAdapters.toResult result
            }

        member _.GetSessionInfo(host: HostRecord, sessionId: string) =
            task {
                let! supervisor = resolveSupervisor host
                let request: GetSessionInfo = { session = sessionId }
                let! info = supervisor.Ask<SessionInfo>(request, TimeSpan.FromSeconds 5.0)
                return FsiSupervisorAdapters.toSessionSnapshot info
            }

        member _.ListSessions(host: HostRecord) =
            task {
                let! supervisor = resolveSupervisor host
                let request: ListSessions = { all = true }
                let! sessions = supervisor.Ask<Sessions>(request, TimeSpan.FromSeconds 5.0)
                return sessions.items |> List.map FsiSupervisorAdapters.toSessionSnapshot
            }

        member _.ResetSession(host: HostRecord, sessionId: string) =
            task {
                let! supervisor = resolveSupervisor host
                let request: ResetSession = { session = sessionId }
                let! result = supervisor.Ask<ResetSessionResult>(request, TimeSpan.FromSeconds 5.0)
                return FsiSupervisorAdapters.toResetResult result
            }
