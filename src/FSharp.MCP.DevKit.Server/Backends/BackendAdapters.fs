namespace FSharp.MCP.DevKit.Server.Backends

open System
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Communication.IPC

module BackendAdapters =
    module BrowserExecutionMetadata =
        [<Literal>]
        let TargetKind = "browser.target.kind"

        [<Literal>]
        let BrowserId = "browser.id"

        [<Literal>]
        let TabId = "browser.tabId"

        [<Literal>]
        let CompanionSessionId = "browser.companion.sessionId"

        [<Literal>]
        let CompanionHostId = "browser.companion.hostId"

        [<Literal>]
        let ExecutionPlane = "browser.executionPlane"

        let private copyIfPresent (sourceKey: string) (targetKey: string) (metadata: Map<string, string>) =
            match metadata.TryFind sourceKey, metadata.ContainsKey targetKey with
            | Some value, false -> metadata.Add(targetKey, value)
            | _ -> metadata

        let normalize (metadata: Map<string, string>) =
            metadata
            |> copyIfPresent "schedule.target.kind" TargetKind
            |> copyIfPresent "schedule.target.browserId" BrowserId
            |> copyIfPresent "schedule.target.tabId" TabId
            |> copyIfPresent "schedule.target.companion.sessionId" CompanionSessionId
            |> copyIfPresent "schedule.target.companion.hostId" CompanionHostId
            |> copyIfPresent "schedule.target.executionPlane" ExecutionPlane

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
          Metadata = BrowserExecutionMetadata.normalize request.Metadata
          Result = result }
