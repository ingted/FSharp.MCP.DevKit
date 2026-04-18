namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.IO
open System.Text.Json
open FSharp.MCP.DevKit.Core

module WinAgentEnvelopeImport =
    type WinAgentOutputEventEnvelope = {
        SequenceNo: int64
        StreamKind: string
        Text: string
        IsReplay: bool
        TimestampUtc: DateTimeOffset
    }

    type WinAgentSharedExecutionEnvelope = {
        SchemaVersion: int
        ExecutionPlane: string
        ExecutionId: string
        RequestId: string
        ToolName: string
        RouteName: string
        Status: string
        StartedAtUtc: DateTimeOffset
        CompletedAtUtc: DateTimeOffset
        Output: string
        Error: string option
        ExceptionType: string option
        Metadata: Map<string, string>
        OutputEvents: WinAgentOutputEventEnvelope list
    }

    type ImportSummary = {
        ImportedCount: int
        ResultIds: string list
        SkippedCount: int
        Errors: string list
    }

    let jsonOptions =
        let options = JsonSerializerOptions()
        options.PropertyNameCaseInsensitive <- true
        options

    let parseEnvelope (json: string) =
        if String.IsNullOrWhiteSpace json then
            invalidArg "json" "WinAgent execution envelope JSON is required."

        JsonSerializer.Deserialize<WinAgentSharedExecutionEnvelope>(json, jsonOptions)

    let tryParseEnvelope (json: string) =
        try
            Some(parseEnvelope json)
        with _ ->
            None

    let readEnvelopeLines (path: string) =
        if String.IsNullOrWhiteSpace path then
            invalidArg "path" "WinAgent execution envelope JSONL path is required."

        if File.Exists path then
            File.ReadLines(path)
            |> Seq.filter (fun line -> not (String.IsNullOrWhiteSpace line))
            |> Seq.toList
        else
            []

    let toMetadata agentId hostId sessionId (envelope: WinAgentSharedExecutionEnvelope) =
        let route =
            { AgentId = agentId
              HostId = hostId
              SessionId = sessionId }

        let addIfMissing key value (metadata: Map<string, string>) =
            if metadata.ContainsKey key then metadata else metadata.Add(key, value)

        envelope.Metadata
        |> Map.add "winagent.schemaVersion" (string envelope.SchemaVersion)
        |> Map.add "winagent.executionPlane" envelope.ExecutionPlane
        |> Map.add "winagent.executionId" envelope.ExecutionId
        |> Map.add "winagent.toolName" envelope.ToolName
        |> Map.add "winagent.routeName" envelope.RouteName
        |> addIfMissing "execution.plane" envelope.ExecutionPlane
        |> addIfMissing "execution.source" "PulseTrade.Mcp.WinAgent"
        |> PrincipalAttribution.normalize route

    let toExecutionRecord agentId hostId sessionId (envelope: WinAgentSharedExecutionEnvelope) =
        let isSuccess = not (String.Equals(envelope.Status, "failed", StringComparison.OrdinalIgnoreCase))
        let executionTime =
            let elapsed = envelope.CompletedAtUtc - envelope.StartedAtUtc
            if elapsed >= TimeSpan.Zero then Some elapsed else None

        { ResultId = envelope.ExecutionId
          RequestId = envelope.RequestId
          AgentId = agentId
          BackendKind = InProc
          HostId = hostId
          SessionId = sessionId
          OperationKind = ExecuteCode
          SubmittedAt = envelope.StartedAtUtc.UtcDateTime
          StartedAt = Some envelope.StartedAtUtc.UtcDateTime
          CompletedAt = Some envelope.CompletedAtUtc.UtcDateTime
          RawErrorType = envelope.ExceptionType
          Metadata = toMetadata agentId hostId sessionId envelope
          Result =
            { Output = if isNull envelope.Output then "" else envelope.Output
              Errors = envelope.Error |> Option.defaultValue ""
              IsSuccess = isSuccess
              ExecutionTime = executionTime
              Diagnostics = [||]
              Value = None } }
