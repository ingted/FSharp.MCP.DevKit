module FSharp.MCP.DevKit.FsiHost.ActorHelpers

open System
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Messages

[<Literal>]
let DefaultSessionId = "default-session"

let toRemoteDiagnostic (diagnostic: FsiDiagnostic) : FsiRemoteDiagnostic =
    { FileName = diagnostic.FileName
      StartLine = diagnostic.StartLine
      EndLine = diagnostic.EndLine
      StartColumn = diagnostic.StartColumn
      EndColumn = diagnostic.EndColumn
      Severity = diagnostic.Severity
      Message = diagnostic.Message }

let toRemoteResult (result: FsiResult) : FsiRemoteResult =
    { Output = result.Output
      Errors = result.Errors
      IsSuccess = result.IsSuccess
      ExecutionTimeMs = result.ExecutionTime |> Option.map (fun value -> value.TotalMilliseconds)
      Diagnostics = result.Diagnostics |> Array.map toRemoteDiagnostic
      Value = result.Value
      RawErrorType = None }

let successResult (output: string) : FsiRemoteResult =
    { Output = output
      Errors = ""
      IsSuccess = true
      ExecutionTimeMs = None
      Diagnostics = [||]
      Value = None
      RawErrorType = None }

let failureResult (error: string) (rawErrorType: string option) : FsiRemoteResult =
    { Output = ""
      Errors = error
      IsSuccess = false
      ExecutionTimeMs = None
      Diagnostics = [||]
      Value = None
      RawErrorType = rawErrorType }

let statusToString (status: SessionStatus) =
    match status with
    | SessionReady -> "SessionReady"
    | SessionBusy -> "SessionBusy"
    | SessionFaulted -> "SessionFaulted"
    | SessionMissing -> "SessionMissing"
