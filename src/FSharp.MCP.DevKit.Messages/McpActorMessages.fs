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

type FsiRemoteCommandRequest =
    { RequestId: string
      CommandType: string
      Payload: string
      UsePackageTargets: bool option }

type FsiRemoteResult =
    { Output: string
      Errors: string
      IsSuccess: bool
      ExecutionTimeMs: float option
      Diagnostics: FsiRemoteDiagnostic array }

type FsiRemoteCommandResponse =
    { RequestId: string
      Result: FsiRemoteResult }
