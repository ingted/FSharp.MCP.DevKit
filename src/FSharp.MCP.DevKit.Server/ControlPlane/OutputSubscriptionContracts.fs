namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open Akka.FSI.Contracts

type OutputSubscriptionApplyResult =
    { Subscription: SessionOutputSubscription
      ReplayEvents: SessionOutputEvent list }

module OutputSubscriptionContracts =
    let normalizeText (value: string) =
        if isNull value then "" else value.Trim()

    let normalizeSequence (value: int64 option) =
        value |> Option.defaultValue 0L |> max 0L

    let toUtcOffset (timestamp: DateTime) =
        match timestamp.Kind with
        | DateTimeKind.Utc -> timestamp
        | DateTimeKind.Local -> timestamp.ToUniversalTime()
        | _ -> DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        |> DateTimeOffset

    let toSubscriberRecord (nowUtc: DateTime) (request: SubscribeSessionOutput) =
        let sessionId = normalizeText request.session
        let subscriberId = normalizeText request.subscriberId

        if String.IsNullOrWhiteSpace sessionId then
            Error "session is required"
        elif String.IsNullOrWhiteSpace subscriberId then
            Error "subscriberId is required"
        else
            Ok
                { SessionId = sessionId
                  SubscriberId = subscriberId
                  FromSequenceNo = normalizeSequence request.fromSequenceNo
                  IncludeHistory = request.includeHistory |> Option.defaultValue false
                  SubscribedAt = nowUtc }

    let toContractEvent (forceReplay: bool option) (eventRecord: OutputEventRecord) =
        { session = eventRecord.SessionId
          executionId = eventRecord.ExecutionId
          sequenceNo = eventRecord.SequenceNo
          streamKind = eventRecord.StreamKind
          timestampUtc = toUtcOffset eventRecord.TimestampUtc
          payload = eventRecord.Payload
          isReplay = forceReplay |> Option.orElse (Some eventRecord.IsReplay) }

    let rejectedSubscription sessionId subscriberId message =
        { session = normalizeText sessionId
          subscriberId = normalizeText subscriberId
          accepted = false
          nextSequenceNo = None
          message = Some message }

    let acceptedSubscription (record: OutputSubscriberRecord) (replayEvents: SessionOutputEvent list) =
        let nextSequenceNo =
            replayEvents
            |> List.tryLast
            |> Option.map (fun eventRecord -> eventRecord.sequenceNo + 1L)
            |> Option.defaultValue record.FromSequenceNo

        { session = record.SessionId
          subscriberId = record.SubscriberId
          accepted = true
          nextSequenceNo = Some nextSequenceNo
          message = None }

    let subscribe (outputStore: IOutputStore) (nowUtc: DateTime) (request: SubscribeSessionOutput) =
        match toSubscriberRecord nowUtc request with
        | Error message ->
            { Subscription = rejectedSubscription request.session request.subscriberId message
              ReplayEvents = [] }
        | Ok record ->
            let subscribed = outputStore.Subscribe record

            let replayEvents =
                if subscribed.IncludeHistory then
                    outputStore.ListEvents(subscribed.SessionId, afterSequenceNo = subscribed.FromSequenceNo)
                    |> List.map (toContractEvent (Some true))
                else
                    []

            { Subscription = acceptedSubscription subscribed replayEvents
              ReplayEvents = replayEvents }

    let unsubscribe (outputStore: IOutputStore) (request: UnsubscribeSessionOutput) =
        let sessionId = normalizeText request.session
        let subscriberId = normalizeText request.subscriberId

        if String.IsNullOrWhiteSpace sessionId then
            rejectedSubscription request.session request.subscriberId "session is required"
        elif String.IsNullOrWhiteSpace subscriberId then
            rejectedSubscription request.session request.subscriberId "subscriberId is required"
        else
            let removed = outputStore.Unsubscribe(sessionId, subscriberId)

            { session = sessionId
              subscriberId = subscriberId
              accepted = removed
              nextSequenceNo = None
              message = if removed then None else Some "subscriber was not registered" }
