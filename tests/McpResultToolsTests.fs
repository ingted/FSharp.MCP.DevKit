module McpResultToolsTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.Integration
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Server.ResultQuery

type private FailOnceArchiveStore(inner: ISessionOutputArchiveStore) =
    let mutable shouldFail = true

    interface ISessionOutputArchiveStore with
        member _.Seal(sessionId: string, events: OutputEventRecord list, archivedAt: DateTime) =
            if shouldFail then
                shouldFail <- false
                raise (InvalidOperationException("tool seal failure"))
            else
                inner.Seal(sessionId, events, archivedAt)

        member _.ListEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            inner.ListEvents(sessionId, ?afterSequenceNo = afterSequenceNo, ?limit = limit)

        member _.ListArchives(?limit: int) =
            inner.ListArchives(?limit = limit)

        member _.TryGetArchive(sessionId: string) = inner.TryGetArchive(sessionId)

        member _.MarkSealPending(sessionId: string, events: OutputEventRecord list, pendingAt: DateTime, errorMessage: string) =
            inner.MarkSealPending(sessionId, events, pendingAt, errorMessage)

        member _.ListPendingEvents(sessionId: string, ?afterSequenceNo: int64, ?limit: int) =
            inner.ListPendingEvents(sessionId, ?afterSequenceNo = afterSequenceNo, ?limit = limit)

        member _.TryGetSealPending(sessionId: string) = inner.TryGetSealPending(sessionId)

        member _.RecoverSealPending(sessionId: string) = inner.RecoverSealPending(sessionId)

        member _.PruneArchives(?keepLatest: int, ?olderThanUtc: DateTime, ?dryRun: bool) =
            inner.PruneArchives(?keepLatest = keepLatest, ?olderThanUtc = olderThanUtc, ?dryRun = dryRun)

type private FakeProcSupervisorClient(startFactory: string * ProcHostSpec -> ProcHostSnapshot, healthFactory: string -> ProcHostSnapshot option) =
    interface IProcSupervisorClient with
        member _.StartProc(procId: string, spec: ProcHostSpec) = Task.FromResult(startFactory (procId, spec))
        member _.StopProc(_, _) = Task.FromException<ProcHostSnapshot>(InvalidOperationException("Not used"))
        member _.GetProcInfo(procId: string) = Task.FromResult(healthFactory procId)
        member _.ListProcInfo() = Task.FromResult([])
        member _.RestartProc(procId: string) =
            match healthFactory procId with
            | Some value -> Task.FromResult(value)
            | None -> Task.FromException<ProcHostSnapshot>(InvalidOperationException("Missing proc"))

type private FakeFsiSupervisorClient(sessionFactory: HostRecord * string -> FsiSupervisorSessionSnapshot) =
    interface IFsiSupervisorClient with
        member _.Execute(host: HostRecord, request: FsiSupervisorExecRequest) =
            Task.FromResult(
                { SessionId = request.SessionId
                  RawErrorType = None
                  Result =
                    { Output = request.Code
                      Errors = ""
                      IsSuccess = true
                      ExecutionTime = Some(TimeSpan.FromMilliseconds 5.0)
                      Diagnostics = [||]
                      Value = None } }
            )

        member _.GetSessionInfo(host: HostRecord, sessionId: string) =
            Task.FromResult(sessionFactory (host, sessionId))

        member _.ListSessions(_) = Task.FromResult([])

        member _.EnsureSession(_, sessionId: string) =
            Task.FromResult(
                { SessionId = sessionId
                  Existed = false
                  Status = "created" }
            )

        member _.ResetSession(_, sessionId: string) =
            Task.FromResult(
                { SessionId = sessionId
                  Existed = true
                  Status = "reset" }
            )

let private createWinAgentEnvelopeJsonWithMetadata executionId requestId (extraMetadata: Map<string, string>) =
    let baseMetadata: Map<string, string> =
        Map.ofList
            [ "execution.plane", "winagent"
              "execution.route", "shared-fsi-host"
              "browser.id", "sharpbrowser" ]

    let metadata =
        extraMetadata
        |> Map.fold (fun state key value -> Map.add key value state) baseMetadata

    let envelope: WinAgentEnvelopeImport.WinAgentSharedExecutionEnvelope =
        { SchemaVersion = 1
          ExecutionPlane = "winagent"
          ExecutionId = executionId
          RequestId = requestId
          ToolName = "sharedFsi.planBrowserCompanion"
          RouteName = "shared-fsi-host"
          Status = "succeeded"
          StartedAtUtc = DateTimeOffset.Parse("2026-04-16T10:00:00Z")
          CompletedAtUtc = DateTimeOffset.Parse("2026-04-16T10:00:01Z")
          Output = "planned companion"
          Error = None
          ExceptionType = None
          Metadata = metadata
          OutputEvents =
            [ ({ SequenceNo = 1L
                 StreamKind = "stdout"
                 Text = "planned companion"
                 IsReplay = false
                 TimestampUtc = DateTimeOffset.Parse("2026-04-16T10:00:01Z") }: WinAgentEnvelopeImport.WinAgentOutputEventEnvelope) ] }

    JsonSerializer.Serialize(envelope, WinAgentEnvelopeImport.jsonOptions)

let private createWinAgentEnvelopeJson executionId requestId =
    createWinAgentEnvelopeJsonWithMetadata executionId requestId Map.empty

let private createIsolatedResultService () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore

    new FsiMcpService(
        NullLogger<FsiMcpService>.Instance,
        enableRemoteClient = false,
        sessionOutputLiveStore = (JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore),
        sessionOutputArchiveStore = (JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore),
        resultRegistry = (InMemoryResultRegistry() :> IResultRegistry)
    )

let private executionRecord resultId agentId hostId sessionId submittedAt metadata =
    { ResultId = resultId
      RequestId = "request-" + resultId
      AgentId = agentId
      BackendKind = InProc
      HostId = hostId
      SessionId = sessionId
      OperationKind = ExecuteCode
      SubmittedAt = submittedAt
      StartedAt = Some submittedAt
      CompletedAt = Some(submittedAt.AddMilliseconds 1.0)
      RawErrorType = None
      Metadata = metadata
      Result = FsiResult.empty }

[<Fact>]
let ``ExecutionStore lists by route metadata and limit`` () =
    let store = InMemoryResultRegistry() :> IExecutionStore
    let baseTime = DateTime.Parse("2026-04-17T08:55:00Z").ToUniversalTime()

    store.Put(executionRecord "r1" "agent-a" "host-a" "session-a" baseTime (Map.ofList [ "principal.id", "codex"; "browser.id", "sb-1" ]))
    store.Put(executionRecord "r2" "agent-a" "host-a" "session-b" (baseTime.AddSeconds 1.0) (Map.ofList [ "principal.id", "gemini"; "browser.id", "sb-1" ]))
    store.Put(executionRecord "r3" "agent-b" "host-b" "session-a" (baseTime.AddSeconds 2.0) (Map.ofList [ "principal.id", "codex"; "browser.id", "sb-2" ]))

    let codexResults = store.List(agentId = "agent-a", metadata = [ "principal.id", "codex" ])
    let sessionResults = store.List(sessionId = "session-a", limit = 1)

    Assert.Single(codexResults) |> ignore
    Assert.Equal("r1", codexResults.Head.ResultId)
    Assert.Single(sessionResults) |> ignore
    Assert.Equal("r3", sessionResults.Head.ResultId)

[<Fact>]
let ``FsiMcpService uses injected execution store for executed records`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let executionStore = InMemoryResultRegistry() :> IExecutionStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = (JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore),
                sessionOutputArchiveStore = (JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore),
                executionStore = executionStore
            )

        use _cleanup = service :> IDisposable

        let! record =
            service.ExecuteOperation(
                ExecuteCode,
                "let executionStoreInjectedValue = 42",
                timeout = TimeSpan.FromSeconds 30.0
            )

        let stored = executionStore.TryGet(record.ResultId)

        let routeResults =
            executionStore.List(
                agentId = "default-agent",
                hostId = "default-host",
                sessionId = "default-session",
                limit = 10
            )

        Assert.True(stored.IsSome)
        Assert.True(routeResults |> List.exists (fun item -> item.ResultId = record.ResultId))
    }

[<Fact>]
let ``JsonLineResultRegistry serializes concurrent writes across instances`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let store1 = JsonLineResultRegistry(tempRoot) :> IResultRegistry
        let store2 = JsonLineResultRegistry(tempRoot) :> IResultRegistry
        let baseTime = DateTime.Parse("2026-04-17T10:18:00Z").ToUniversalTime()

        let records =
            [ 1..20 ]
            |> List.map (fun index ->
                executionRecord
                    $"concurrent-{index}"
                    "agent-concurrent"
                    "host-concurrent"
                    "session-concurrent"
                    (baseTime.AddMilliseconds(float index))
                    Map.empty)

        let writes =
            records
            |> List.mapi (fun index record ->
                Task.Run(fun () ->
                    if index % 2 = 0 then
                        store1.Put record
                    else
                        store2.Put record))

        do! Task.WhenAll(writes)

        let reloaded = JsonLineResultRegistry(tempRoot) :> IExecutionStore
        let results = reloaded.List(agentId = "agent-concurrent", limit = 50)

        Assert.Equal(records.Length, results.Length)
        Assert.True(records |> List.forall (fun record -> results |> List.exists (fun stored -> stored.ResultId = record.ResultId)))
    }

[<Fact>]
let ``McpResultTools get list query compare and resources work`` () =
    task {
        let service = createIsolatedResultService ()
        use _cleanup = service :> IDisposable

        let! _ = service.ExecuteOperation(ExecuteCode, "let resultQueryValue = 10", timeout = TimeSpan.FromSeconds 30.0)
        let! first = service.ExecuteOperation(EvaluateExpression, "resultQueryValue", timeout = TimeSpan.FromSeconds 30.0)
        let! _ = service.ExecuteOperation(ExecuteCode, "let resultQueryValue = 11", timeout = TimeSpan.FromSeconds 30.0)
        let! second = service.ExecuteOperation(EvaluateExpression, "resultQueryValue", timeout = TimeSpan.FromSeconds 30.0)

        let singleJson = McpResultTools.GetFsiResult(service, "default-agent", first.ResultId)
        let listJson = McpResultTools.ListFsiResults(service, "default-agent", "", "")
        let executionStoreSingleJson = McpResultTools.GetExecutionStoreRecord(service, "default-agent", first.ResultId)
        let executionStoreListJson = McpResultTools.ListExecutionStoreRecords(service, "default-agent", "", "")

        let mapJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "map",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "value",
                "",
                ""
            )

        let compareJson =
            McpResultTools.CompareFsiResults(
                service,
                "default-agent",
                first.ResultId,
                second.ResultId,
                "value",
                ""
            )

        let fsharpJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "map",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "records1 |> Seq.map (fun record -> record.Result.Value |> Option.defaultValue \"\") |> Seq.toList",
                "fsharpCode",
                ""
            )

        let filterMaterializedJson =
            McpResultTools.QueryFsiResults(
                service,
                "default-agent",
                "filter",
                $"{first.ResultId}\n{second.ResultId}",
                "",
                "isSuccess",
                "",
                "syntheticResult"
            )

        let resultResource = ResultResources(service)
        let resultResourceJson = resultResource.Result(first.ResultId)
        let agentResultsJson = resultResource.AgentResults("default-agent")
        let sessionResultsJson = resultResource.SessionResults("default-host", "default-session")
        let sessionIdResultsJson = resultResource.SessionResultsBySessionId("default-session")
        let sessionIdToolJson = McpResultTools.ListFsiResultsBySessionId(service, "default-session")
        let executionStoreSessionIdToolJson = McpResultTools.ListExecutionStoreRecordsBySessionId(service, "default-session")

        let single = FSharpJson.deserialize<FsiExecutionRecord option> singleJson
        let listed = FSharpJson.deserialize<FsiExecutionRecord list> listJson
        let sessionIdListed = FSharpJson.deserialize<FsiExecutionRecord list> sessionIdToolJson
        let mapResponse = FSharpJson.deserialize<ResultQueryResponse> mapJson
        let compareResponse = FSharpJson.deserialize<ResultQueryResponse> compareJson
        let fsharpResponse = FSharpJson.deserialize<ResultQueryResponse> fsharpJson
        let materializedResponse = FSharpJson.deserialize<ResultQueryResponse> filterMaterializedJson
        let synthetic = materializedResponse.ProducedResultIds |> List.head |> fun resultId -> service.TryGetResult(resultId)

        Assert.Equal(singleJson, executionStoreSingleJson)
        Assert.Equal(listJson, executionStoreListJson)
        Assert.Equal(sessionIdToolJson, executionStoreSessionIdToolJson)
        Assert.True(single.IsSome)
        Assert.Equal(first.ResultId, single.Value.ResultId)
        Assert.True(listed |> List.exists (fun value -> value.ResultId = first.ResultId))
        Assert.True(listed |> List.exists (fun value -> value.ResultId = second.ResultId))
        Assert.True(mapResponse.IsSuccess)
        Assert.Equal("[\"10\",\"11\"]", mapResponse.MaterializedJson.Value)
        Assert.True(compareResponse.IsSuccess)
        Assert.True(compareResponse.MaterializedJson.IsSome)
        Assert.Contains("\"leftValue\":\"10\"", compareResponse.MaterializedJson.Value)
        Assert.Contains("\"rightValue\":\"11\"", compareResponse.MaterializedJson.Value)
        Assert.True(fsharpResponse.IsSuccess)
        Assert.Equal("[\"10\",\"11\"]", fsharpResponse.MaterializedJson.Value)
        Assert.True(materializedResponse.IsSuccess)
        Assert.Single(materializedResponse.ProducedResultIds) |> ignore
        Assert.True(synthetic.IsSome)
        Assert.Equal(ResultQuery, synthetic.Value.OperationKind)
        Assert.Contains(first.ResultId, resultResourceJson)
        Assert.Contains(first.ResultId, agentResultsJson)
        Assert.Contains(second.ResultId, sessionResultsJson)
        Assert.Contains(second.ResultId, sessionIdResultsJson)
        Assert.True(sessionIdListed |> List.exists (fun value -> value.ResultId = second.ResultId))
    }

[<Fact>]
let ``McpResultTools import WinAgent execution envelope into result and output fabric`` () =
    task {
        let service = createIsolatedResultService ()
        use _cleanup = service :> IDisposable

        let envelopeJson = createWinAgentEnvelopeJson "winagent-result-1" "winagent-request-1"

        let importedJson =
            McpResultTools.ImportWinAgentExecutionEnvelope(
                service,
                "winagent-agent-single",
                "winagent-host-single",
                "winagent-session-single",
                envelopeJson
            )

        let listedJson = McpResultTools.ListFsiResults(service, "winagent-agent-single", "winagent-host-single", "winagent-session-single")
        let outputJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "winagent-agent-single",
                "winagent-host-single",
                "winagent-session-single",
                0L,
                0
            )

        let imported = FSharpJson.deserialize<FsiExecutionRecord> importedJson
        let listed = FSharpJson.deserialize<FsiExecutionRecord list> listedJson
        let outputEvents = FSharpJson.deserialize<OutputEventRecord list> outputJson

        Assert.Equal("winagent-result-1", imported.ResultId)
        Assert.Equal("winagent-agent-single", imported.AgentId)
        Assert.Equal("winagent-host-single", imported.HostId)
        Assert.Equal("winagent-session-single", imported.SessionId)
        Assert.Equal("winagent-agent-single", imported.Metadata[PrincipalAttribution.PrincipalId])
        Assert.Equal("agent", imported.Metadata[PrincipalAttribution.PrincipalKind])
        Assert.Equal("route", imported.Metadata[PrincipalAttribution.PrincipalSource])
        Assert.Equal("winagent-agent-single", imported.Metadata[PrincipalAttribution.PrincipalAgentId])
        Assert.Equal("winagent-host-single", imported.Metadata[PrincipalAttribution.PrincipalHostId])
        Assert.Equal("winagent-session-single", imported.Metadata[PrincipalAttribution.PrincipalSessionId])
        Assert.Equal("winagent", imported.Metadata["winagent.executionPlane"])
        Assert.Equal("PulseTrade.Mcp.WinAgent", imported.Metadata["execution.source"])
        Assert.Equal("sharpbrowser", imported.Metadata["browser.id"])
        Assert.True(imported.Result.IsSuccess)
        Assert.Contains(listed, fun record -> record.ResultId = "winagent-result-1")
        Assert.Single(outputEvents) |> ignore
        Assert.Equal("planned companion", outputEvents[0].Payload)
        Assert.Equal(Some "winagent-result-1", outputEvents[0].ExecutionId)
    }

[<Fact>]
let ``McpResultTools import envelope preserves explicit execution source metadata`` () =
    task {
        let service = createIsolatedResultService ()
        use _cleanup = service :> IDisposable

        let envelopeJson =
            createWinAgentEnvelopeJsonWithMetadata
                "mgmt2-direct-result-1"
                "mgmt2-direct-request-1"
                (Map.ofList [ "execution.source", "Mgmt2.DirectProcSupervisor" ])

        let importedJson =
            McpResultTools.ImportWinAgentExecutionEnvelope(
                service,
                "PulseTrade.Management2",
                "procnode-01",
                "s2",
                envelopeJson
            )

        let imported = FSharpJson.deserialize<FsiExecutionRecord> importedJson

        Assert.Equal("Mgmt2.DirectProcSupervisor", imported.Metadata["execution.source"])
        Assert.Equal("winagent", imported.Metadata["execution.plane"])
        Assert.Equal("PulseTrade.Management2", imported.Metadata[PrincipalAttribution.PrincipalId])
    }

[<Fact>]
let ``McpResultTools import WinAgent execution envelope is idempotent for same route`` () =
    task {
        let service = createIsolatedResultService ()
        use _cleanup = service :> IDisposable

        let envelopeJson = createWinAgentEnvelopeJson "winagent-result-idempotent-1" "winagent-request-idempotent-1"

        let firstJson =
            McpResultTools.ImportWinAgentExecutionEnvelope(
                service,
                "winagent-agent-idempotent",
                "winagent-host-idempotent",
                "winagent-session-idempotent",
                envelopeJson
            )

        let secondJson =
            McpResultTools.ImportWinAgentExecutionEnvelope(
                service,
                "winagent-agent-idempotent",
                "winagent-host-idempotent",
                "winagent-session-idempotent",
                envelopeJson
            )

        let listedJson =
            McpResultTools.ListFsiResults(
                service,
                "winagent-agent-idempotent",
                "winagent-host-idempotent",
                "winagent-session-idempotent"
            )

        let outputJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "winagent-agent-idempotent",
                "winagent-host-idempotent",
                "winagent-session-idempotent",
                0L,
                0
            )

        let first = FSharpJson.deserialize<FsiExecutionRecord> firstJson
        let second = FSharpJson.deserialize<FsiExecutionRecord> secondJson
        let listed = FSharpJson.deserialize<FsiExecutionRecord list> listedJson
        let outputEvents = FSharpJson.deserialize<OutputEventRecord list> outputJson

        Assert.Equal(first.ResultId, second.ResultId)
        Assert.Single(listed) |> ignore
        Assert.Equal("winagent-result-idempotent-1", listed[0].ResultId)
        Assert.Single(outputEvents) |> ignore
        Assert.Equal(Some "winagent-result-idempotent-1", outputEvents[0].ExecutionId)
    }

[<Fact>]
let ``McpResultTools import WinAgent execution envelope rejects duplicate result on different route`` () =
    task {
        let service = createIsolatedResultService ()
        use _cleanup = service :> IDisposable

        let envelopeJson = createWinAgentEnvelopeJson "winagent-result-idempotent-conflict" "winagent-request-idempotent-conflict"

        let _ =
            McpResultTools.ImportWinAgentExecutionEnvelope(
                service,
                "winagent-agent-conflict-a",
                "winagent-host-conflict-a",
                "winagent-session-conflict-a",
                envelopeJson
            )

        let ex =
            Assert.Throws<InvalidOperationException>(fun () ->
                McpResultTools.ImportWinAgentExecutionEnvelope(
                    service,
                    "winagent-agent-conflict-b",
                    "winagent-host-conflict-b",
                    "winagent-session-conflict-b",
                    envelopeJson
                )
                |> ignore)

        Assert.Contains("already imported", ex.Message)
    }

[<Fact>]
let ``McpResultTools import WinAgent execution envelopes from JSONL`` () =
    task {
        let service = createIsolatedResultService ()
        use _cleanup = service :> IDisposable
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let jsonlPath = Path.Combine(tempRoot, "winagent-envelopes.jsonl")
        let first = createWinAgentEnvelopeJson "winagent-jsonl-1" "winagent-jsonl-request-1"
        let second = createWinAgentEnvelopeJson "winagent-jsonl-2" "winagent-jsonl-request-2"
        File.WriteAllLines(jsonlPath, [| first; "not-json"; second |])

        let summaryJson =
            McpResultTools.ImportWinAgentExecutionEnvelopesFromJsonl(
                service,
                "winagent-agent-jsonl",
                "winagent-host-jsonl",
                "winagent-session-jsonl",
                jsonlPath,
                0
            )

        let listedJson = McpResultTools.ListFsiResultsBySessionId(service, "winagent-session-jsonl")

        let summary = FSharpJson.deserialize<WinAgentEnvelopeImport.ImportSummary> summaryJson
        let listed = FSharpJson.deserialize<FsiExecutionRecord list> listedJson

        Assert.Equal(2, summary.ImportedCount)
        Assert.Equal(1, summary.SkippedCount)
        Assert.Contains("winagent-jsonl-1", summary.ResultIds)
        Assert.Contains("winagent-jsonl-2", summary.ResultIds)
        Assert.True(listed |> List.exists (fun record -> record.ResultId = "winagent-jsonl-1"))
        Assert.True(listed |> List.exists (fun record -> record.ResultId = "winagent-jsonl-2"))
    }

[<Fact>]
let ``McpResultTools output tools and resources expose live broker state`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore,
                executionStore = (JsonLineResultRegistry(tempRoot) :> IExecutionStore)
            )

        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()

        let subscribeJson =
            McpResultTools.SubscribeSessionOutput(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "mgmt2-reader",
                0L,
                true
            )

        let _ = service.PublishSessionOutput("stdout", "alpha", executionId = "exec-out-1")
        let _ = service.PublishSessionOutput("stderr", "beta", executionId = "exec-out-1")

        let subscribersJson =
            McpResultTools.ListSessionOutputSubscribers(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let outputJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                0L,
                0
            )

        let outputAfterJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                1L,
                0
            )

        let resultResource = ResultResources(service)
        let outputResourceJson = resultResource.SessionOutput("default-host", "default-session")
        let outputAfterResourceJson = resultResource.SessionOutputAfter("default-host", "default-session", 1L)
        let subscribersResourceJson = resultResource.SessionOutputSubscribers("default-host", "default-session")

        let subscribed = FSharpJson.deserialize<OutputSubscriberRecord> subscribeJson
        let subscribers = FSharpJson.deserialize<OutputSubscriberRecord list> subscribersJson
        let events = FSharpJson.deserialize<OutputEventRecord list> outputJson
        let eventsAfter = FSharpJson.deserialize<OutputEventRecord list> outputAfterJson
        let resourceEvents = FSharpJson.deserialize<OutputEventRecord list> outputResourceJson
        let resourceEventsAfter = FSharpJson.deserialize<OutputEventRecord list> outputAfterResourceJson
        let resourceSubscribers = FSharpJson.deserialize<OutputSubscriberRecord list> subscribersResourceJson

        Assert.Equal("mgmt2-reader", subscribed.SubscriberId)
        Assert.Single(subscribers) |> ignore
        Assert.Equal(2, events.Length)
        Assert.Equal(1L, events[0].SequenceNo)
        Assert.Equal(2L, events[1].SequenceNo)
        Assert.Single(eventsAfter) |> ignore
        Assert.Equal("beta", eventsAfter[0].Payload)
        Assert.Equal(events.Length, resourceEvents.Length)
        Assert.Single(resourceEventsAfter) |> ignore
        Assert.Single(resourceSubscribers) |> ignore
        let unsubscribeJson =
            McpResultTools.UnsubscribeSessionOutput(
                service,
                "default-agent",
                "default-host",
                "default-session",
                "mgmt2-reader"
            )

        let unsubscribed = FSharpJson.deserialize<bool> unsubscribeJson
        Assert.True(unsubscribed)
    }

[<Fact>]
let ``McpResultTools session output resources keep same read path after archive seal`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore,
                executionStore = (JsonLineResultRegistry(tempRoot) :> IExecutionStore)
            )

        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()

        let _ = service.PublishSessionOutput("stdout", "archived-alpha", executionId = "exec-archive-2")
        let _ = service.PublishSessionOutput("stderr", "archived-beta", executionId = "exec-archive-2")
        let archive =
            match service.SealSessionOutputArchive() with
            | Archived value -> value
            | SealPending pending -> failwithf "expected archived outcome but got pending: %s" pending.ErrorMessage

        let outputJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                0L,
                0
            )

        let resultResource = ResultResources(service)
        let outputResourceJson = resultResource.SessionOutput("default-host", "default-session")
        let outputAfterResourceJson = resultResource.SessionOutputAfter("default-host", "default-session", 1L)

        let events = FSharpJson.deserialize<OutputEventRecord list> outputJson
        let resourceEvents = FSharpJson.deserialize<OutputEventRecord list> outputResourceJson
        let resourceEventsAfter = FSharpJson.deserialize<OutputEventRecord list> outputAfterResourceJson

        Assert.Equal("default-session", archive.SessionId)
        Assert.Equal(2, archive.EventCount)
        Assert.Equal(2, events.Length)
        Assert.Equal(events.Length, resourceEvents.Length)
        Assert.Equal<int64 array>([| 1L; 2L |], resourceEvents |> List.map (fun eventRecord -> eventRecord.SequenceNo) |> List.toArray)
        Assert.Single(resourceEventsAfter) |> ignore
        Assert.Equal("archived-beta", resourceEventsAfter[0].Payload)
    }

[<Fact>]
let ``McpResultTools seal pending tool and resource expose status and recovery`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore =
            FailOnceArchiveStore(JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore)
            :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore,
                executionStore = (JsonLineResultRegistry(tempRoot) :> IExecutionStore)
            )

        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()

        let _ = service.PublishSessionOutput("stdout", "pending-alpha", executionId = "exec-pending-tool")
        let! _ = service.ExecuteOperation(ResetSession, "", timeout = TimeSpan.FromSeconds 30.0)

        let pendingJson =
            McpResultTools.GetSessionOutputSealPending(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let resultResource = ResultResources(service)
        let pendingResourceJson = resultResource.SessionOutputSealPending("default-host", "default-session")

        let recoveredJson =
            McpResultTools.RecoverSessionOutputSealPending(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let eventsJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                0L,
                0
            )

        let pending = FSharpJson.deserialize<SessionOutputSealPendingRecord option> pendingJson
        let pendingResource = FSharpJson.deserialize<SessionOutputSealPendingRecord option> pendingResourceJson
        let recovered = FSharpJson.deserialize<SessionOutputArchiveRecord option> recoveredJson
        let events = FSharpJson.deserialize<OutputEventRecord list> eventsJson

        Assert.True(pending.IsSome)
        Assert.True(pendingResource.IsSome)
        Assert.Contains("tool seal failure", pending.Value.ErrorMessage)
        Assert.True(recovered.IsSome)
        Assert.Equal(1, recovered.Value.EventCount)
        Assert.Single(events) |> ignore
        Assert.Equal("pending-alpha", events[0].Payload)
    }

[<Fact>]
let ``McpResultTools explicit seal session output archives live events without lifecycle reset`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore
            )

        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()

        let _ = service.PublishSessionOutput("stdout", "seal-alpha", executionId = "exec-seal-tool")
        let _ = service.PublishSessionOutput("stderr", "seal-beta", executionId = "exec-seal-tool")

        let sealedJson =
            McpResultTools.SealSessionOutput(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let eventsJson =
            McpResultTools.GetSessionOutputEvents(
                service,
                "default-agent",
                "default-host",
                "default-session",
                0L,
                0
            )

        let sealOutcome = FSharpJson.deserialize<SessionOutputSealOutcome> sealedJson
        let events = FSharpJson.deserialize<OutputEventRecord list> eventsJson
        let archive = service.TryGetSessionOutputArchive()

        match sealOutcome with
        | Archived archived ->
            Assert.Equal("default-session", archived.SessionId)
            Assert.Equal(2, archived.EventCount)
        | SealPending pending ->
            failwithf "expected archived outcome but got pending: %s" pending.ErrorMessage

        Assert.True(archive.IsSome)
        Assert.Equal(2, archive.Value.EventCount)
        Assert.Equal(2, events.Length)
        Assert.Equal("seal-alpha", events[0].Payload)
        Assert.Equal("seal-beta", events[1].Payload)
    }

[<Fact>]
let ``McpResultTools unregister fsi session seals archive and removes live lookup`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore
            )

        use _cleanup = service :> IDisposable
        let route = service.ResolveRoute()
        let _ = service.PublishSessionOutput("stdout", "tool-unregister", executionId = "exec-unregister-tool", requestedRoute = route)

        let unregisterJson =
            McpResultTools.UnregisterFsiSession(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let eventsJson =
            McpResultTools.GetArchivedSessionOutputEvents(
                service,
                "default-session",
                0L,
                0
            )

        let result = FSharpJson.deserialize<SessionUnregisterResult option> unregisterJson
        let events = FSharpJson.deserialize<OutputEventRecord list> eventsJson

        Assert.True(result.IsSome)
        Assert.True(service.TryGetSession("default-host", "default-session").IsNone)
        Assert.Single(events) |> ignore
        Assert.Equal("tool-unregister", events[0].Payload)
    }

[<Fact>]
let ``McpResultTools archive tool and resource expose archive metadata after explicit seal`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore
            )

        use _cleanup = service :> IDisposable
        let _ = service.ResolveRoute()
        let _ = service.PublishSessionOutput("stdout", "archive-alpha", executionId = "exec-archive-tool")

        let _ =
            McpResultTools.SealSessionOutput(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let archiveJson =
            McpResultTools.GetSessionOutputArchive(
                service,
                "default-agent",
                "default-host",
                "default-session"
            )

        let resourceArchiveJson =
            let resources = ResultResources(service)
            resources.SessionOutputArchive("default-host", "default-session")

        let archiveListJson = McpResultTools.ListSessionOutputArchives(service, 0)

        let resourceArchiveListJson =
            let resources = ResultResources(service)
            resources.SessionOutputArchives()

        let archivedEventsJson =
            let resources = ResultResources(service)
            resources.ArchivedSessionOutput("default-session")

        let archive = FSharpJson.deserialize<SessionOutputArchiveRecord option> archiveJson
        let resourceArchive = FSharpJson.deserialize<SessionOutputArchiveRecord option> resourceArchiveJson
        let archiveList = FSharpJson.deserialize<SessionOutputArchiveRecord list> archiveListJson
        let resourceArchiveList = FSharpJson.deserialize<SessionOutputArchiveRecord list> resourceArchiveListJson
        let archivedEvents = FSharpJson.deserialize<OutputEventRecord list> archivedEventsJson

        Assert.True(archive.IsSome)
        Assert.True(resourceArchive.IsSome)
        Assert.Equal("default-session", archive.Value.SessionId)
        Assert.Equal(1, archive.Value.EventCount)
        Assert.Equal(archive.Value.EventCount, resourceArchive.Value.EventCount)
        Assert.Single(archiveList) |> ignore
        Assert.Single(resourceArchiveList) |> ignore
        Assert.Equal("default-session", archiveList[0].SessionId)
        Assert.Equal("archive-alpha", archivedEvents[0].Payload)
    }

[<Fact>]
let ``McpResultTools prune session output archives supports dry run and execute`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore
            )

        use _cleanup = service :> IDisposable

        let oldSession = "session-prune-old"
        let newSession = "session-prune-new"

        let _ =
            archiveStore.Seal(
                oldSession,
                [ { SessionId = oldSession
                    ExecutionId = Some "exec-prune-old"
                    SequenceNo = 1L
                    StreamKind = "stdout"
                    TimestampUtc = DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc)
                    Payload = "old"
                    IsReplay = false } ],
                DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc)
            )

        let _ =
            archiveStore.Seal(
                newSession,
                [ { SessionId = newSession
                    ExecutionId = Some "exec-prune-new"
                    SequenceNo = 1L
                    StreamKind = "stdout"
                    TimestampUtc = DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc)
                    Payload = "new"
                    IsReplay = false } ],
                DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc)
            )

        let dryRunJson =
            McpResultTools.PruneSessionOutputArchives(
                service,
                1,
                "2026-04-18T00:00:00Z",
                true
            )

        let executeJson =
            McpResultTools.PruneSessionOutputArchives(
                service,
                1,
                "2026-04-18T00:00:00Z",
                false
            )

        let dryRun = FSharpJson.deserialize<SessionOutputArchivePruneReport> dryRunJson
        let executed = FSharpJson.deserialize<SessionOutputArchivePruneReport> executeJson
        let remaining = service.ListSessionOutputArchives()

        Assert.True(dryRun.DryRun)
        Assert.Equal(1, dryRun.CandidateCount)
        Assert.Equal(oldSession, dryRun.Candidates[0].SessionId)
        Assert.False(executed.DryRun)
        Assert.Equal(1, executed.DeletedCount)
        Assert.Empty(executed.Errors)
        Assert.Single(remaining) |> ignore
        Assert.Equal(newSession, remaining[0].SessionId)
    }

[<Fact>]
let ``McpResultTools list and session resources survive result registry reload`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore

        let service1 =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = (JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore),
                sessionOutputArchiveStore = (JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore),
                resultRegistry = (JsonLineResultRegistry(tempRoot) :> IResultRegistry)
            )

        use _cleanup1 = service1 :> IDisposable

        let! _ = service1.ExecuteOperation(ExecuteCode, "let persistedToolValue = 99", timeout = TimeSpan.FromSeconds 30.0)
        let! evalRecord = service1.ExecuteOperation(EvaluateExpression, "persistedToolValue", timeout = TimeSpan.FromSeconds 30.0)

        let service2 =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = (JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore),
                sessionOutputArchiveStore = (JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore),
                resultRegistry = (JsonLineResultRegistry(tempRoot) :> IResultRegistry)
            )

        use _cleanup2 = service2 :> IDisposable
        let _ = service2.ResolveRoute()

        let listJson = McpResultTools.ListFsiResults(service2, "default-agent", "", "")
        let singleJson = McpResultTools.GetFsiResult(service2, "default-agent", evalRecord.ResultId)
        let resources = ResultResources(service2)
        let sessionResultsJson = resources.SessionResults("default-host", "default-session")

        let listed = FSharpJson.deserialize<FsiExecutionRecord list> listJson
        let single = FSharpJson.deserialize<FsiExecutionRecord option> singleJson
        let sessionResults = FSharpJson.deserialize<FsiExecutionRecord list> sessionResultsJson

        Assert.True(single.IsSome)
        Assert.Equal(Some "99", single.Value.Result.Value)
        Assert.True(listed |> List.exists (fun record -> record.ResultId = evalRecord.ResultId))
        Assert.True(sessionResults |> List.exists (fun record -> record.ResultId = evalRecord.ResultId))
    }

[<Fact>]
let ``ResultResources resolve host-session route from registry instead of assuming default agent`` () =
    task {
        let tempRoot = Path.Combine(Path.GetTempPath(), "PulseTrade.McpResultToolsTests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        let liveStore = JsonLineSessionOutputLiveStore(tempRoot) :> ISessionOutputLiveStore
        let archiveStore = JsonLineSessionOutputArchiveStore(tempRoot) :> ISessionOutputArchiveStore

        let procClient =
            FakeProcSupervisorClient(
                (fun (procId, spec) ->
                    { ProcId = procId
                      Status = "running"
                      ProcessId = Some 9400
                      FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9400/user/fsi/supervisor"
                      NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9400"
                      LastProbeUtc = Some DateTime.UtcNow
                      LastProbeOk = Some true
                      ProbeFailures = 0
                      Spec = Some spec
                      LastError = None }),
                (fun procId ->
                    Some
                        { ProcId = procId
                          Status = "running"
                          ProcessId = Some 9400
                          FsiSupervisorPath = Some "akka.tcp://FsiExecutionSystem@localhost:9400/user/fsi/supervisor"
                          NodeAddress = Some "akka.tcp://FsiExecutionSystem@localhost:9400"
                          LastProbeUtc = Some DateTime.UtcNow
                          LastProbeOk = Some true
                          ProbeFailures = 0
                          Spec = None
                          LastError = None })
            )

        let fsiClient =
            FakeFsiSupervisorClient(fun (_, sessionId) ->
                { SessionId = sessionId
                  Status = "ready"
                  Refs = []
                  Loads = []
                  SearchPaths = []
                  Variables = []
                  LastCheckpointId = None
                  RunningSinceUtc = Some DateTime.UtcNow })

        let service =
            new FsiMcpService(
                NullLogger<FsiMcpService>.Instance,
                enableRemoteClient = false,
                sessionOutputLiveStore = liveStore,
                sessionOutputArchiveStore = archiveStore,
                procSupervisorClient = (procClient :> IProcSupervisorClient),
                fsiSupervisorClient = (fsiClient :> IFsiSupervisorClient)
            )
        use _cleanup = service :> IDisposable

        let _ = McpControlPlaneTools.RegisterFsiAgent(service, "agent-output", "Agent Output")

        let! _ =
            McpControlPlaneTools.CreateFsiHost(
                service,
                "agent-output",
                "net10",
                "dotnet",
                "--dll\nfsi-host.dll",
                "/srv/fsi",
                "host-output",
                "",
                0
            )

        let! _ = McpControlPlaneTools.CreateFsiSession(service, "agent-output", "host-output", "session-output", "Session Output")

        let route =
            { AgentId = "agent-output"
              HostId = "host-output"
              SessionId = "session-output" }

        let _ = service.PublishSessionOutput("stdout", "projected-output", requestedRoute = route, executionId = "exec-output")

        let resources = ResultResources(service)
        let eventsJson = resources.SessionOutput("host-output", "session-output")
        let pendingJson = resources.SessionOutputSealPending("host-output", "session-output")

        let events = FSharpJson.deserialize<OutputEventRecord list> eventsJson
        let pending = FSharpJson.deserialize<SessionOutputSealPendingRecord option> pendingJson

        Assert.Single(events) |> ignore
        Assert.Equal("projected-output", events[0].Payload)
        Assert.True(pending.IsNone)
    }
