namespace FSharp.MCP.DevKit.Server.Backends

open System
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core

type ExecutionRequest =
    { RequestId: string
      Route: ExecutionRoute
      OperationKind: OperationKind
      Payload: string
      Timeout: TimeSpan option
      UsePackageTargets: bool option
      Metadata: Map<string, string> }

type BackendHealth =
    { BackendKind: BackendKind
      IsAvailable: bool
      Message: string option
      HostId: string option
      CheckedAt: DateTime }

type IFsiExecutionBackend =
    abstract member BackendKind: BackendKind
    abstract member Execute: ExecutionRequest -> Task<FsiExecutionRecord>
    abstract member EnsureSession: ExecutionRoute -> Task<SessionRecord>
    abstract member GetSessionState: ExecutionRoute -> Task<SessionRecord>
    abstract member ResetSession: ExecutionRoute -> Task<FsiExecutionRecord>
    abstract member RestartHost: HostRecord -> Task<unit>
    abstract member HealthCheck: HostRecord -> Task<BackendHealth>
