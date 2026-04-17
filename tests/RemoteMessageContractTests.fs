module RemoteMessageContractTests

open System
open System.IO
open Xunit
open Akka.FSI.Contracts
open FSharp.MCP.DevKit.Messages
open FSharp.MCP.DevKit.Server.ControlPlane

[<Fact>]
let ``FsiRemoteCommandRequest carries route and timeout`` () =
    let request =
        { RequestId = "req-1"
          CommandType = "EXEC"
          Payload = "printfn \"hi\""
          Route =
            Some
                { AgentId = Some "agent-1"
                  HostId = Some "host-1"
                  SessionId = Some "session-1" }
          UsePackageTargets = Some true
          TimeoutMs = Some 30000 }

    Assert.Equal(Some "agent-1", request.Route |> Option.bind (fun route -> route.AgentId))
    Assert.Equal(Some "host-1", request.Route |> Option.bind (fun route -> route.HostId))
    Assert.Equal(Some "session-1", request.Route |> Option.bind (fun route -> route.SessionId))
    Assert.Equal(Some 30000, request.TimeoutMs)

[<Fact>]
let ``FsiRemoteResult carries value and raw error type`` () =
    let result =
        { Output = ""
          Errors = "boom"
          IsSuccess = false
          ExecutionTimeMs = Some 12.0
          Diagnostics = [||]
          Value = Some "42"
          RawErrorType = Some "RemoteExecutionError" }

    Assert.Equal(Some "42", result.Value)
    Assert.Equal(Some "RemoteExecutionError", result.RawErrorType)

[<Fact>]
let ``FsiRemoteCommandResponse carries host and session ids`` () =
    let response =
        { RequestId = "req-2"
          HostId = Some "host-2"
          SessionId = Some "session-2"
          Result =
            { Output = "ok"
              Errors = ""
              IsSuccess = true
              ExecutionTimeMs = None
              Diagnostics = [||]
              Value = None
              RawErrorType = None }
          SessionState =
            Some
                { SessionId = "session-2"
                  SessionName = "session-2"
                  Status = "SessionReady"
                  Refs = []
                  Loads = []
                  SearchPaths = []
                  Variables = []
                  LastCheckpointId = None
                  RunningSinceUtc = None
                  LastExecutionAt = None } }

    Assert.Equal(Some "host-2", response.HostId)
    Assert.Equal(Some "session-2", response.SessionId)
    Assert.True(response.Result.IsSuccess)
    Assert.True(response.SessionState.IsSome)

[<Fact>]
let ``BrowserInventorySnapshotDto carries browser companion and tab summaries`` () =
    let observedAt = DateTime.Parse("2026-04-16T15:45:00Z").ToUniversalTime()
    let snapshot =
        { ObservedAtUtc = observedAt
          Items =
            [ { BrowserId = "browser-01"
                DisplayName = Some "SharpBrowser Desk"
                HostId = Some "desk-01"
                MachineName = Some "TRADER-DESK-01"
                ProcessId = Some 12345
                Status = "Ready"
                CompanionSession =
                    Some
                        { AgentId = Some "winagent"
                          HostId = Some "desk-01-winagent"
                          SessionId = "desk-01-browser-01-browser"
                          ExecutionPlane = Some "winagent-shared-fsi-host" }
                Tabs =
                    [ { TabId = "tab-active"
                        Title = Some "Market News"
                        Url = Some "https://example.test/news"
                        IsActive = true
                        LastObservedUtc = Some observedAt } ]
                Tags = [ "sharpbrowser"; "browser-companion" ]
                RegisteredAtUtc = observedAt.AddMinutes(-1.0)
                LastHeartbeatUtc = Some observedAt } ] }

    let browser = snapshot.Items.Head
    let companion = browser.CompanionSession.Value
    let tab = browser.Tabs.Head

    Assert.Equal("browser-01", browser.BrowserId)
    Assert.Equal(Some "desk-01-winagent", companion.HostId)
    Assert.Equal("desk-01-browser-01-browser", companion.SessionId)
    Assert.Equal(Some "https://example.test/news", tab.Url)
    Assert.True(tab.IsActive)
    Assert.Contains("browser-companion", browser.Tags)

[<Fact>]
let ``SubscribeSessionOutput contract attaches subscriber and returns replay events`` () =
    let outputStore =
        SessionOutputStore(
            InMemoryOutputSubscriberBroker() :> IOutputSubscriberBroker,
            JsonLineSessionOutputLiveStore(Path.Combine(Path.GetTempPath(), "PulseTrade.WBS71", Guid.NewGuid().ToString("N")))
            :> ISessionOutputLiveStore)
        :> IOutputStore

    let eventRecord, _ =
        outputStore.Publish(
            { SessionId = "session-1"
              ExecutionId = Some "exec-1"
              SequenceNo = 0L
              StreamKind = "stdout"
              TimestampUtc = DateTime.SpecifyKind(DateTime.Parse("2026-04-18T01:58:00Z"), DateTimeKind.Utc)
              Payload = "hello"
              IsReplay = false })

    let result =
        OutputSubscriptionContracts.subscribe
            outputStore
            (DateTime.SpecifyKind(DateTime.Parse("2026-04-18T01:59:00Z"), DateTimeKind.Utc))
            { session = " session-1 "
              subscriberId = " mgmt2 "
              fromSequenceNo = Some 0L
              includeHistory = Some true }

    let subscribers = outputStore.ListSubscribers("session-1")

    Assert.True(result.Subscription.accepted)
    Assert.Equal("session-1", result.Subscription.session)
    Assert.Equal("mgmt2", result.Subscription.subscriberId)
    Assert.Equal(Some 2L, result.Subscription.nextSequenceNo)
    Assert.Single(result.ReplayEvents) |> ignore
    Assert.Equal(eventRecord.Payload, result.ReplayEvents.Head.payload)
    Assert.Equal(Some true, result.ReplayEvents.Head.isReplay)
    Assert.Single(subscribers) |> ignore

[<Fact>]
let ``UnsubscribeSessionOutput contract removes subscriber and reports missing subscriber`` () =
    let outputStore =
        SessionOutputStore(InMemoryOutputSubscriberBroker() :> IOutputSubscriberBroker)
        :> IOutputStore

    let _ =
        OutputSubscriptionContracts.subscribe
            outputStore
            DateTime.UtcNow
            { session = "session-2"
              subscriberId = "codex"
              fromSequenceNo = None
              includeHistory = None }

    let removed =
        OutputSubscriptionContracts.unsubscribe
            outputStore
            { session = "session-2"
              subscriberId = "codex" }

    let missing =
        OutputSubscriptionContracts.unsubscribe
            outputStore
            { session = "session-2"
              subscriberId = "codex" }

    Assert.True(removed.accepted)
    Assert.False(missing.accepted)
    Assert.Equal(Some "subscriber was not registered", missing.message)
