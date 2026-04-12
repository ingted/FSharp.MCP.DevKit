namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Collections.Concurrent
open System.Threading

type InMemoryInventoryEventStore() =
    let items = ConcurrentQueue<InventoryEventRecord>()
    let mutable nextSequenceId = 0L

    interface IInventoryEventStore with
        member _.Append(record: InventoryEventRecord) =
            let normalized =
                { record with
                    SequenceId = Interlocked.Increment(&nextSequenceId) }

            items.Enqueue(normalized)
            normalized

        member _.List(?afterSequenceId: int64, ?limit: int) =
            let afterSequenceId = defaultArg afterSequenceId 0L
            let limit = defaultArg limit Int32.MaxValue

            items.ToArray()
            |> Array.filter (fun item -> item.SequenceId > afterSequenceId)
            |> Array.sortBy (fun item -> item.SequenceId)
            |> Array.truncate limit
            |> Array.toList
