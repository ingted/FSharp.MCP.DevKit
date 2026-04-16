namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Collections.Generic
open System.IO
open System.Text
open FSharp.MCP.DevKit.Core

type ScheduledExecutionStatus =
    | ScheduledPending
    | ScheduledRunning
    | ScheduledCompleted
    | ScheduledFailed
    | ScheduledCancelled

type ScheduledExecutionItem =
    { ScheduleId: string
      Route: ExecutionRoute
      OperationKind: OperationKind
      Payload: string
      DueAtUtc: DateTime
      Timeout: TimeSpan option
      Metadata: Map<string, string>
      CreatedAtUtc: DateTime
      StartedAtUtc: DateTime option
      CompletedAtUtc: DateTime option
      Status: ScheduledExecutionStatus
      ResultId: string option
      RetryCount: int
      LastError: string option }

type ScheduledExecutionProcessResult =
    { Item: ScheduledExecutionItem
      Result: FsiExecutionRecord option }

type ScheduledExecutionQueue(?executionStoreRoot: string) =
    let gate = obj()
    let items = Dictionary<string, ScheduledExecutionItem>(StringComparer.OrdinalIgnoreCase)
    let executionStoreRoot =
        executionStoreRoot
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> Option.defaultWith SessionOutputArchivePath.resolveExecutionStoreRoot
    let scheduleRoot = Path.Combine(executionStoreRoot, "scheduled")
    let scheduleJournalPath = Path.Combine(scheduleRoot, "queue.jsonl")

    let ensureDirectory () =
        Directory.CreateDirectory(scheduleRoot) |> ignore

    let persist (item: ScheduledExecutionItem) =
        ensureDirectory ()
        let line = FSharpJson.serialize item + Environment.NewLine
        File.AppendAllText(scheduleJournalPath, line, Encoding.UTF8)

    let loadPersisted () =
        if File.Exists scheduleJournalPath then
            File.ReadLines(scheduleJournalPath, Encoding.UTF8)
            |> Seq.filter (fun line -> not (String.IsNullOrWhiteSpace line))
            |> Seq.iter (fun line ->
                try
                    let item = FSharpJson.deserialize<ScheduledExecutionItem> line
                    items[item.ScheduleId] <- item
                with _ ->
                    ())

    let ordered (values: seq<ScheduledExecutionItem>) =
        values
        |> Seq.sortBy (fun item -> item.DueAtUtc, item.CreatedAtUtc, item.ScheduleId)
        |> Seq.toList

    do
        lock gate loadPersisted

    member _.Enqueue
        (
            route: ExecutionRoute,
            operationKind: OperationKind,
            payload: string,
            dueAtUtc: DateTime,
            timeout: TimeSpan option,
            metadata: Map<string, string>
        ) =
        let now = DateTime.UtcNow

        let item =
            { ScheduleId = Guid.NewGuid().ToString("N")
              Route = route
              OperationKind = operationKind
              Payload = payload
              DueAtUtc = dueAtUtc.ToUniversalTime()
              Timeout = timeout
              Metadata = metadata
              CreatedAtUtc = now
              StartedAtUtc = None
              CompletedAtUtc = None
              Status = ScheduledPending
              ResultId = None
              RetryCount = 0
              LastError = None }

        lock gate (fun () ->
            items[item.ScheduleId] <- item
            persist item)
        item

    member _.List(?route: ExecutionRoute, ?status: ScheduledExecutionStatus) =
        lock gate (fun () ->
            items.Values
            |> Seq.filter (fun item ->
                match route with
                | Some value ->
                    item.Route.AgentId = value.AgentId
                    && item.Route.HostId = value.HostId
                    && item.Route.SessionId = value.SessionId
                | None -> true)
            |> Seq.filter (fun item ->
                match status with
                | Some value -> item.Status = value
                | None -> true)
            |> ordered)

    member _.TryGet(scheduleId: string) =
        lock gate (fun () ->
            match items.TryGetValue scheduleId with
            | true, item -> Some item
            | _ -> None)

    member _.TryStartNextDue(observedAtUtc: DateTime) =
        lock gate (fun () ->
            let now = observedAtUtc.ToUniversalTime()

            let due =
                items.Values
                |> Seq.filter (fun item -> item.Status = ScheduledPending && item.DueAtUtc <= now)
                |> ordered
                |> List.tryHead

            match due with
            | None -> None
            | Some item ->
                let running =
                    { item with
                        Status = ScheduledRunning
                        StartedAtUtc = Some now
                        LastError = None }

                items[item.ScheduleId] <- running
                persist running
                Some running)

    member _.Complete(scheduleId: string, record: FsiExecutionRecord) =
        lock gate (fun () ->
            match items.TryGetValue scheduleId with
            | true, item ->
                let completed =
                    { item with
                        Status = ScheduledCompleted
                        ResultId = Some record.ResultId
                        CompletedAtUtc = Some(record.CompletedAt |> Option.defaultValue DateTime.UtcNow)
                        LastError = None }

                items[scheduleId] <- completed
                persist completed
                completed
            | _ -> invalidOp $"Scheduled execution '{scheduleId}' was not found.")

    member _.Fail(scheduleId: string, error: string, ?resultId: string) =
        lock gate (fun () ->
            match items.TryGetValue scheduleId with
            | true, item ->
                let failed =
                    { item with
                        Status = ScheduledFailed
                        CompletedAtUtc = Some DateTime.UtcNow
                        ResultId = resultId
                        LastError = Some error }

                items[scheduleId] <- failed
                persist failed
                failed
            | _ -> invalidOp $"Scheduled execution '{scheduleId}' was not found.")

    member _.Cancel(scheduleId: string, reason: string option) =
        lock gate (fun () ->
            match items.TryGetValue scheduleId with
            | true, item ->
                match item.Status with
                | ScheduledCompleted -> invalidOp $"Scheduled execution '{scheduleId}' is already completed."
                | ScheduledCancelled -> item
                | _ ->
                    let cancelled =
                        { item with
                            Status = ScheduledCancelled
                            CompletedAtUtc = Some DateTime.UtcNow
                            LastError = reason }

                    items[scheduleId] <- cancelled
                    persist cancelled
                    cancelled
            | _ -> invalidOp $"Scheduled execution '{scheduleId}' was not found.")

    member _.RequeueFailed(scheduleId: string, dueAtUtc: DateTime) =
        lock gate (fun () ->
            match items.TryGetValue scheduleId with
            | true, item ->
                match item.Status with
                | ScheduledFailed ->
                    let requeued =
                        { item with
                            DueAtUtc = dueAtUtc.ToUniversalTime()
                            StartedAtUtc = None
                            CompletedAtUtc = None
                            Status = ScheduledPending
                            ResultId = None
                            RetryCount = item.RetryCount + 1
                            LastError = None }

                    items[scheduleId] <- requeued
                    persist requeued
                    requeued
                | _ -> invalidOp $"Scheduled execution '{scheduleId}' is not failed and cannot be requeued."
            | _ -> invalidOp $"Scheduled execution '{scheduleId}' was not found.")

    member _.RequeueFailedWithBackoff(scheduleId: string, baseDelay: TimeSpan, maxDelay: TimeSpan, observedAtUtc: DateTime) =
        lock gate (fun () ->
            match items.TryGetValue scheduleId with
            | true, item ->
                match item.Status with
                | ScheduledFailed ->
                    let retryCount = item.RetryCount + 1
                    let exponent = max 0 (retryCount - 1)
                    let rawSeconds = baseDelay.TotalSeconds * Math.Pow(2.0, float exponent)
                    let delay = TimeSpan.FromSeconds(min rawSeconds maxDelay.TotalSeconds)

                    let requeued =
                        { item with
                            DueAtUtc = observedAtUtc.ToUniversalTime().Add(delay)
                            StartedAtUtc = None
                            CompletedAtUtc = None
                            Status = ScheduledPending
                            ResultId = None
                            RetryCount = retryCount
                            LastError = None }

                    items[scheduleId] <- requeued
                    persist requeued
                    requeued
                | _ -> invalidOp $"Scheduled execution '{scheduleId}' is not failed and cannot be requeued."
            | _ -> invalidOp $"Scheduled execution '{scheduleId}' was not found.")
