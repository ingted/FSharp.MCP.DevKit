namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open System.Collections.Concurrent
open FSharp.MCP.DevKit.Messages

type InMemoryBrowserInventoryRegistry() =
    let browsers = ConcurrentDictionary<string, BrowserInventoryDto>(StringComparer.OrdinalIgnoreCase)

    let observedAt (browser: BrowserInventoryDto) =
        browser.LastHeartbeatUtc
        |> Option.defaultValue browser.RegisteredAtUtc

    interface IBrowserInventoryRegistry with
        member _.Upsert(browser: BrowserInventoryDto) =
            if String.IsNullOrWhiteSpace browser.BrowserId then
                invalidArg "browser.BrowserId" "BrowserId is required."

            let normalized =
                { browser with
                    Status =
                        if String.IsNullOrWhiteSpace browser.Status then
                            "unknown"
                        else
                            browser.Status.Trim()
                    Tags =
                        browser.Tags
                        |> List.filter (fun tag -> not (String.IsNullOrWhiteSpace tag))
                        |> List.map (fun tag -> tag.Trim())
                        |> List.distinctBy (fun tag -> tag.ToLowerInvariant()) }

            browsers.[normalized.BrowserId] <- normalized
            normalized

        member _.TryGet(browserId: string) =
            match browsers.TryGetValue browserId with
            | true, browser -> Some browser
            | false, _ -> None

        member _.List(?status: string, ?tag: string, ?limit: int) =
            let statusFilter =
                status
                |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                |> Option.map (fun value -> value.Trim())

            let tagFilter =
                tag
                |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                |> Option.map (fun value -> value.Trim())

            let items =
                browsers.Values
                |> Seq.filter (fun browser ->
                    match statusFilter with
                    | Some value -> browser.Status.Equals(value, StringComparison.OrdinalIgnoreCase)
                    | None -> true)
                |> Seq.filter (fun browser ->
                    match tagFilter with
                    | Some value ->
                        browser.Tags
                        |> List.exists (fun tag -> tag.Equals(value, StringComparison.OrdinalIgnoreCase))
                    | None -> true)
                |> Seq.sortByDescending observedAt
                |> fun values ->
                    match limit with
                    | Some value when value > 0 -> values |> Seq.truncate value
                    | _ -> values
                |> Seq.toList

            { Items = items
              ObservedAtUtc = DateTime.UtcNow }

        member _.Remove(browserId: string) =
            match browsers.TryRemove browserId with
            | true, _ -> true
            | false, _ -> false
