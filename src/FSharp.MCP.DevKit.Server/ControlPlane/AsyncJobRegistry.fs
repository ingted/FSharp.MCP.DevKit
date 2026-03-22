namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Collections.Concurrent
open FSharp.MCP.DevKit.Core

type InMemoryAsyncJobRegistry() =
    let jobs = ConcurrentDictionary<string, AsyncFsiJob>()

    interface IAsyncJobRegistry with
        member _.Create(job: AsyncFsiJob) =
            jobs.[job.AsyncId] <- job
            job

        member _.MarkRunning(asyncId: string, startedAt: DateTime) =
            jobs.AddOrUpdate(
                asyncId,
                (fun _ -> failwith $"Async job '{asyncId}' was not found."),
                (fun _ existing ->
                    { existing with
                        StartedAt = Some startedAt
                        Status = Running })
            )
            |> ignore

        member _.Complete(asyncId: string, resultId: string, result: FsiResult, completedAt: DateTime) =
            jobs.AddOrUpdate(
                asyncId,
                (fun _ -> failwith $"Async job '{asyncId}' was not found."),
                (fun _ existing ->
                    { existing with
                        CompletedAt = Some completedAt
                        Status = Completed
                        ResultId = Some resultId
                        Result = Some result })
            )
            |> ignore

        member _.Fail(asyncId: string, result: FsiResult, completedAt: DateTime) =
            jobs.AddOrUpdate(
                asyncId,
                (fun _ -> failwith $"Async job '{asyncId}' was not found."),
                (fun _ existing ->
                    { existing with
                        CompletedAt = Some completedAt
                        Status = Failed
                        Result = Some result })
            )
            |> ignore

        member _.TryGet(asyncId: string) =
            match jobs.TryGetValue asyncId with
            | true, record -> Some record
            | false, _ -> None

        member _.ListByRoute(route: ExecutionRoute) =
            jobs.Values
            |> Seq.filter (fun job -> job.Route = route)
            |> Seq.sortByDescending (fun job -> job.SubmittedAt)
            |> Seq.toList
