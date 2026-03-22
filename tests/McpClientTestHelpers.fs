module FSharp.MCP.DevKit.Tests.McpClientTestHelpers

open System
open System.Text.Json
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server
open Xunit

let withClient testBody =
    task {
        let! client = McpClientHarness.createStdioClientAsync()

        try
            return! testBody client
        finally
            (client :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

let waitForAsyncStatus (client: McpClientSession) (asyncId: string) =
    let rec poll attempt =
        task {
            let! status = client.ReadResourceJsonAsync<AsyncFsiStatusDto>($"fsi/async/{asyncId}")

            if status.Exists && status.IsCompleted then
                return status
            elif attempt >= 60 then
                let stderr = String.concat "\n" client.StderrLog
                return raise (TimeoutException($"Timed out waiting for async status '{asyncId}'. STDERR:\n{stderr}"))
            else
                do! Task.Delay(100)
                return! poll (attempt + 1)
        }

    poll 0

let parseJson<'T> (json: string) = JsonSerializer.Deserialize<'T>(json)

let assertContains (expected: string) (values: seq<string>) =
    Assert.Contains(expected, values)

let bootstrapDefaultRoute (client: McpClientSession) =
    client.CallToolTextAsync(
        "execute_f_sharp_code",
        McpClientHarness.arguments [ "code", box "let bootstrapDefault = 0"
                                     "timeoutSeconds", box 30 ]
    )

let createDefaultSession (client: McpClientSession) (sessionId: string) =
    client.CallToolJsonAsync<SessionRecord>(
        "create_fsi_session",
        McpClientHarness.arguments [ "agentId", box "default-agent"
                                     "hostId", box "default-host"
                                     "sessionId", box sessionId ]
    )
