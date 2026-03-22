namespace FSharp.MCP.DevKit.Server.Backends

open System
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Communication.IPC

module BackendAdapters =
    let createFailedResult (error: string) (executionTime: TimeSpan option) (rawErrorType: string option) =
        let suffix =
            rawErrorType
            |> Option.map (fun value -> $"\nRawErrorType: {value}")
            |> Option.defaultValue ""

        { Output = ""
          Errors = error + suffix
          IsSuccess = false
          Value = None
          ExecutionTime = executionTime
          Diagnostics = [||] }

    let toCoreDiagnostic (diagnostic: PipeDiagnostic) : FsiDiagnostic =
        { FileName = diagnostic.FileName
          StartLine = diagnostic.StartLine
          EndLine = diagnostic.EndLine
          StartColumn = diagnostic.StartColumn
          EndColumn = diagnostic.EndColumn
          Severity = diagnostic.Severity
          Message = diagnostic.Message }

    let toFsiResult (response: PipeResponse) : FsiResult =
        { Output = response.Output
          Errors = response.Errors
          IsSuccess = response.IsSuccess
          Value = response.Value
          ExecutionTime = response.ExecutionTime
          Diagnostics = response.Diagnostics |> Option.defaultValue [||] |> Array.map toCoreDiagnostic }

    let inferRawErrorType (response: PipeResponse) =
        if response.IsSuccess then
            None
        elif String.IsNullOrWhiteSpace response.Errors then
            Some "UnknownRemoteError"
        else
            Some "RemoteExecutionError"

    let toExecutionRecord
        (backendKind: BackendKind)
        (request: ExecutionRequest)
        (submittedAt: DateTime)
        (startedAt: DateTime option)
        (completedAt: DateTime option)
        (hostId: string)
        (sessionId: string)
        (resultId: string)
        (result: FsiResult)
        (rawErrorType: string option)
        =
        { ResultId = resultId
          RequestId = request.RequestId
          AgentId = request.Route.AgentId
          BackendKind = backendKind
          HostId = hostId
          SessionId = sessionId
          OperationKind = request.OperationKind
          SubmittedAt = submittedAt
          StartedAt = startedAt
          CompletedAt = completedAt
          RawErrorType = rawErrorType
          Result = result }
