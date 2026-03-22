namespace FSharp.MCP.DevKit.Messages

open System

type FsiRemoteDiagnostic =
    { FileName: string
      StartLine: int
      EndLine: int
      StartColumn: int
      EndColumn: int
      Severity: string
      Message: string }

type FsiRemoteRouteDto =
    { AgentId: string option
      HostId: string option
      SessionId: string option }

type FsiRemoteCommandRequest =
    { RequestId: string
      CommandType: string
      Payload: string
      Route: FsiRemoteRouteDto option
      UsePackageTargets: bool option
      TimeoutMs: int option }

type FsiRemoteResult =
    { Output: string
      Errors: string
      IsSuccess: bool
      ExecutionTimeMs: float option
      Diagnostics: FsiRemoteDiagnostic array
      Value: string option
      RawErrorType: string option }

type FsiRemoteSessionState =
    { SessionId: string
      SessionName: string
      Status: string
      Refs: string list
      Loads: string list
      SearchPaths: string list
      Variables: (string * string) list
      LastCheckpointId: string option
      RunningSinceUtc: DateTime option
      LastExecutionAt: DateTime option }

type FsiRemoteCommandResponse =
    { RequestId: string
      HostId: string option
      SessionId: string option
      Result: FsiRemoteResult
      SessionState: FsiRemoteSessionState option }
