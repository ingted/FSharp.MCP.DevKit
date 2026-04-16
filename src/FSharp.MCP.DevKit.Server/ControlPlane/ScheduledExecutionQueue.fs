namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Collections.Generic
open FSharp.MCP.DevKit.Core

type ScheduledExecutionStatus =
    | ScheduledPending
    | ScheduledRunning
    | ScheduledCompleted
    | ScheduledFailed

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
      LastError: string option }

type ScheduledExecutionProcessResult =
    { Item: ScheduledExecutionItem
      Result: FsiExecutionRecord option }

type ScheduledExecutionQueue() =
    let gate = obj()
    let items = Dictionary<string, ScheduledExecutionItem>(StringComparer.OrdinalIgnoreCase)

    let ordered (values: seq<ScheduledExecutionItem>) =
        values
        |> Seq.sortBy (fun item -> item.DueAtUtc, item.CreatedAtUtc, item.ScheduleId)
        |> Seq.toList

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
              LastError = None }

        lock gate (fun () -> items[item.ScheduleId] <- item)
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
                completed
            | _ -> invalidOp $"Scheduled execution '{scheduleId}' was not found.")

    member _.Fail(scheduleId: string, error: string) =
        lock gate (fun () ->
            match items.TryGetValue scheduleId with
            | true, item ->
                let failed =
                    { item with
                        Status = ScheduledFailed
                        CompletedAtUtc = Some DateTime.UtcNow
                        LastError = Some error }

                items[scheduleId] <- failed
                failed
            | _ -> invalidOp $"Scheduled execution '{scheduleId}' was not found.")
