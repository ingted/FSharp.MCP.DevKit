namespace FSharp.MCP.DevKit.Core

open System
open FSharp.Compiler.Diagnostics

type FsiDiagnostic =
    { FileName: string
      StartLine: int
      EndLine: int
      StartColumn: int
      EndColumn: int
      Severity: string
      Message: string }

module FsiDiagnostic =
    let ofCompilerDiagnostic (diagnostic: FSharpDiagnostic) =
        { FileName = diagnostic.FileName
          StartLine = diagnostic.StartLine
          EndLine = diagnostic.EndLine
          StartColumn = diagnostic.StartColumn
          EndColumn = diagnostic.EndColumn
          Severity = diagnostic.Severity.ToString()
          Message = diagnostic.Message }

type FsiResult =
    { Output: string
      Errors: string
      IsSuccess: bool
      ExecutionTime: TimeSpan option
      Diagnostics: FsiDiagnostic array
      Value: string option }

module FsiResult =
    let empty =
        { Output = ""
          Errors = ""
          IsSuccess = true
          ExecutionTime = None
          Diagnostics = [||]
          Value = None }

type BackendKind =
    | InProc
    | NetFxRemote
    | Net10Remote

type OperationKind =
    | ExecuteCode
    | EvaluateExpression
    | LoadScript
    | ReferenceAssembly
    | ReferenceNuget
    | AddSearchPath
    | ResetSession
    | RestartHost
    | GetState
    | ResultQuery

type FsiExecutionRecord =
    { ResultId: string
      RequestId: string
      AgentId: string
      BackendKind: BackendKind
      HostId: string
      SessionId: string
      OperationKind: OperationKind
      SubmittedAt: DateTime
      StartedAt: DateTime option
      CompletedAt: DateTime option
      RawErrorType: string option
      Result: FsiResult }
