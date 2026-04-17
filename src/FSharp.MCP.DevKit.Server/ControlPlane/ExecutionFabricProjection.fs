namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open Akka.FSI.Contracts
open FSharp.MCP.DevKit.Core

module ExecutionFabricProjection =
    let private dateTimeOffsetUtc (value: DateTime) =
        let utc =
            match value.Kind with
            | DateTimeKind.Utc -> value
            | DateTimeKind.Local -> value.ToUniversalTime()
            | _ -> DateTime.SpecifyKind(value, DateTimeKind.Utc)

        DateTimeOffset(utc)

    let private nonBlank value =
        value
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private textOrNone (value: string) =
        if String.IsNullOrWhiteSpace value then None else Some value

    let private metadataValue key (metadata: Map<string, string>) =
        metadata |> Map.tryFind key |> nonBlank

    let private withContext (record: FsiExecutionRecord) (envelope: SerializedResultEnvelope) =
        ResultSerialization.withExecutionContext (Some record.ResultId) (Some record.SessionId) envelope

    let private resultValueObject (record: FsiExecutionRecord) =
        record.Result.Value
        |> nonBlank
        |> Option.map box

    let serializeResultEnvelope (serializer: IResultSerializer) (record: FsiExecutionRecord) =
        async {
            match resultValueObject record with
            | None -> return None
            | Some value ->
                try
                    let! fsPickler = serializer.TrySerializeFsPickler value

                    match fsPickler with
                    | Some envelope -> return Some(withContext record envelope)
                    | None ->
                        let! protobuf = serializer.TrySerializeProtobufFSharp value

                        match protobuf with
                        | Some envelope -> return Some(withContext record envelope)
                        | None ->
                            let ex =
                                InvalidOperationException(
                                    "No result serializer accepted the FSI result value. Falling back to diagnostic envelope."
                                )

                            return Some(serializer.ToFailureEnvelope(value, ex) |> withContext record)
                with ex ->
                    return Some(serializer.ToFailureEnvelope(value, ex) |> withContext record)
        }

    let toOutputEvent (eventRecord: OutputEventRecord) =
        { sessionId = eventRecord.SessionId
          executionId = eventRecord.ExecutionId
          sequenceNo = eventRecord.SequenceNo
          streamKind = eventRecord.StreamKind
          timestampUtc = dateTimeOffsetUtc eventRecord.TimestampUtc
          payload = eventRecord.Payload
          isReplay = eventRecord.IsReplay
          metadata = [] }

    let toTarget (record: FsiExecutionRecord) =
        { agentId = record.AgentId
          hostId = record.HostId
          sessionId = record.SessionId
          browserId = record.Metadata |> metadataValue "browser.id"
          tabId = record.Metadata |> metadataValue "browser.tabId"
          executionPlane = record.Metadata |> metadataValue "browser.executionPlane"
          metadata = record.Metadata |> Map.toList }

    let toExecutionFabricRecordWithEnvelope
        (envelope: SerializedResultEnvelope option)
        (outputEvents: OutputEventRecord list)
        (record: FsiExecutionRecord)
        =
        { schemaVersion = 1
          executionId = record.ResultId
          requestId = record.RequestId
          operationKind = string record.OperationKind
          backendKind = string record.BackendKind
          target = toTarget record
          status = if record.Result.IsSuccess then "succeeded" else "failed"
          submittedAtUtc = dateTimeOffsetUtc record.SubmittedAt
          startedAtUtc = record.StartedAt |> Option.map dateTimeOffsetUtc
          completedAtUtc = record.CompletedAt |> Option.map dateTimeOffsetUtc
          valueText = record.Result.Value |> nonBlank
          stdoutText = textOrNone record.Result.Output
          stderrText = textOrNone record.Result.Errors
          errorType = record.RawErrorType
          errorMessage = if record.Result.IsSuccess then None else textOrNone record.Result.Errors
          resultEnvelope = envelope
          metadata = record.Metadata |> Map.toList
          outputEvents = outputEvents |> List.map toOutputEvent }

    let toExecutionFabricRecord (serializer: IResultSerializer) (outputEvents: OutputEventRecord list) (record: FsiExecutionRecord) =
        async {
            let! envelope = serializeResultEnvelope serializer record
            return toExecutionFabricRecordWithEnvelope envelope outputEvents record
        }
