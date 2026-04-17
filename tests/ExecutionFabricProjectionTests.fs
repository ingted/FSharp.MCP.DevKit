module ExecutionFabricProjectionTests

open System
open System.Threading.Tasks
open Akka.FSI.Contracts
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.McpFsiTools
open Microsoft.Extensions.Logging.Abstractions
open Xunit

type private InMemoryOutputStore() =
    let events = ResizeArray<OutputEventRecord>()
    let subscribers = ResizeArray<OutputSubscriberRecord>()
    let mutable sequenceNo = 0L

    interface IOutputStore with
        member _.Subscribe(record: OutputSubscriberRecord) =
            subscribers.Add record
            record

        member _.Unsubscribe(sessionId: string, subscriberId: string) =
            let index =
                subscribers
                |> Seq.tryFindIndex (fun item -> item.SessionId = sessionId && item.SubscriberId = subscriberId)

            match index with
            | Some value ->
                subscribers.RemoveAt value
                true
            | None -> false

        member _.ListSubscribers(sessionId: string) =
            subscribers
            |> Seq.filter (fun item -> item.SessionId = sessionId)
            |> Seq.toList

        member _.Publish(record: OutputEventRecord) =
            sequenceNo <- sequenceNo + 1L
            let stored = { record with SequenceNo = sequenceNo }
            events.Add stored
            stored, []

        member _.ListEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            let afterSequenceNo = defaultArg afterSequenceNo 0L
            let limit = defaultArg limit Int32.MaxValue

            events
            |> Seq.filter (fun item -> item.SessionId = sessionId && item.SequenceNo > afterSequenceNo)
            |> Seq.truncate limit
            |> Seq.toList

        member _.ClearSession(sessionId: string) =
            let matching =
                events
                |> Seq.filter (fun item -> item.SessionId = sessionId)
                |> Seq.toArray

            matching |> Array.iter (fun item -> events.Remove item |> ignore)
            matching.Length

let private sampleRecord value =
    { ResultId = "result-1"
      RequestId = "request-1"
      AgentId = "agent-1"
      BackendKind = InProc
      HostId = "host-1"
      SessionId = "session-1"
      OperationKind = EvaluateExpression
      SubmittedAt = DateTime(2026, 4, 17, 6, 0, 0, DateTimeKind.Utc)
      StartedAt = Some(DateTime(2026, 4, 17, 6, 0, 1, DateTimeKind.Utc))
      CompletedAt = Some(DateTime(2026, 4, 17, 6, 0, 2, DateTimeKind.Utc))
      RawErrorType = None
      Metadata =
        [ "browser.id", "sb-main"
          "browser.tabId", "tab-1"
          "browser.executionPlane", "winagent-shared-fsi-host" ]
        |> Map.ofList
      Result =
        { Output = "stdout text"
          Errors = ""
          IsSuccess = true
          ExecutionTime = Some(TimeSpan.FromMilliseconds 42.0)
          Diagnostics = [||]
          Value = value } }

[<Fact>]
let ``ExecutionFabricProjection serializes FSI value into shared result envelope`` () =
    task {
        let record = sampleRecord (Some "42")

        let outputEvents =
            [ { SessionId = "session-1"
                ExecutionId = Some "result-1"
                SequenceNo = 1L
                StreamKind = "stdout"
                TimestampUtc = DateTime(2026, 4, 17, 6, 0, 2, DateTimeKind.Utc)
                Payload = "stdout text"
                IsReplay = false } ]

        let! projected =
            ExecutionFabricProjection.toExecutionFabricRecord
                (ResultSerialization.createDefault())
                outputEvents
                record
            |> Async.StartAsTask

        Assert.Equal("result-1", projected.executionId)
        Assert.Equal("EvaluateExpression", projected.operationKind)
        Assert.Equal("InProc", projected.backendKind)
        Assert.Equal("succeeded", projected.status)
        Assert.Equal("sb-main", projected.target.browserId.Value)
        Assert.Equal("tab-1", projected.target.tabId.Value)
        Assert.Equal("42", projected.valueText.Value)
        Assert.Equal("stdout text", projected.stdoutText.Value)
        Assert.Single(projected.outputEvents) |> ignore

        match projected.resultEnvelope with
        | Some envelope ->
            Assert.Equal(Some "result-1", envelope.executionId)
            Assert.Equal(Some "session-1", envelope.session)
            Assert.Equal(ResultSerialization.FsPicklerSerializer, envelope.serializer)
            Assert.True(envelope.payloadBase64.IsSome)
        | None ->
            failwith "expected result envelope"
    } :> Task

[<Fact>]
let ``ExecutionFabricProjection emits diagnostic envelope when serializers decline value`` () =
    task {
        let serializer =
            { new IResultSerializer with
                member _.TrySerializeFsPickler(_) = async.Return None
                member _.TrySerializeProtobufFSharp(_) = async.Return None
                member _.ToFailureEnvelope(value, ex) = ResultSerialization.failureEnvelope "test" value ex }

        let! projected =
            ExecutionFabricProjection.toExecutionFabricRecord
                serializer
                []
                (sampleRecord (Some "diagnostic-value"))
            |> Async.StartAsTask

        match projected.resultEnvelope with
        | Some envelope ->
            Assert.Equal(ResultSerialization.FallbackSerializer, envelope.serializer)
            Assert.Equal(Some "result-1", envelope.executionId)
            Assert.Equal(Some "session-1", envelope.session)
            Assert.True(envelope.serializationError.Value.Contains("No result serializer accepted"))
            Assert.True(envelope.fallbackText.Value.Contains("diagnostic"))
        | None ->
            failwith "expected diagnostic envelope"
    } :> Task

[<Fact>]
let ``ExecutionFabricProjection omits result envelope when FSI value is absent`` () =
    task {
        let! projected =
            ExecutionFabricProjection.toExecutionFabricRecord
                (ResultSerialization.createDefault())
                []
                (sampleRecord None)
            |> Async.StartAsTask

        Assert.True(projected.resultEnvelope.IsNone)
        Assert.True(projected.valueText.IsNone)
        Assert.Equal("stdout text", projected.stdoutText.Value)
    } :> Task

[<Fact>]
let ``FsiMcpService exposes stored result as shared execution fabric record`` () =
    task {
        let resultRegistry = InMemoryResultRegistry() :> IResultRegistry
        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                resultRegistry = resultRegistry,
                outputStore = (InMemoryOutputStore() :> IOutputStore)
            )

        let record =
            { sampleRecord (Some "service-value") with
                AgentId = DefaultRouting.DefaultAgentId
                HostId = DefaultRouting.DefaultHostId
                SessionId = DefaultRouting.DefaultSessionId }

        resultRegistry.Put record

        service.PublishSessionOutput("stdout", "service stdout", executionId = record.ResultId) |> ignore

        let! projected = service.TryGetExecutionFabricRecordForAgent(record.AgentId, record.ResultId)

        match projected with
        | Some fabricRecord ->
            Assert.Equal(record.ResultId, fabricRecord.executionId)
            Assert.Equal("service-value", fabricRecord.valueText.Value)
            Assert.Equal("service stdout", fabricRecord.outputEvents[0].payload)
            Assert.True(fabricRecord.resultEnvelope.IsSome)
        | None ->
            failwith "expected execution fabric record"
    } :> Task

[<Fact>]
let ``get_execution_fabric_record MCP tool serializes shared fabric projection`` () =
    task {
        let resultRegistry = InMemoryResultRegistry() :> IResultRegistry
        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                resultRegistry = resultRegistry,
                outputStore = (InMemoryOutputStore() :> IOutputStore)
            )

        let record =
            { sampleRecord (Some "tool-value") with
                AgentId = DefaultRouting.DefaultAgentId
                HostId = DefaultRouting.DefaultHostId
                SessionId = DefaultRouting.DefaultSessionId }

        resultRegistry.Put record

        let! json = McpResultTools.GetExecutionFabricRecord(service, record.AgentId, record.ResultId)

        Assert.Contains("\"executionId\":\"result-1\"", json)
        Assert.Contains("\"serializer\":\"FsPickler\"", json)
        Assert.Contains("\"valueText\":\"tool-value\"", json)
    } :> Task
