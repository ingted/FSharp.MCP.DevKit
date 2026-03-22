module FSharp.MCP.DevKit.Tests.McpClientTestHelpers

open System
open System.Diagnostics
open System.IO
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
    client.WaitForAsyncStatusAsync(asyncId)

let parseJson<'T> (json: string) = FSharpJson.deserialize<'T> json

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

let repoRoot () = McpClientHarness.resolveRepositoryRoot None

let demoClientProjectPath () =
    Path.Combine(repoRoot (), "examples", "FSharp.MCP.DevKit.DemoClient", "FSharp.MCP.DevKit.DemoClient.fsproj")

let demoClientDllPath () =
    Path.Combine(
        repoRoot (),
        "examples",
        "FSharp.MCP.DevKit.DemoClient",
        "bin",
        "Debug",
        "net10.0",
        "FSharp.MCP.DevKit.DemoClient.dll"
    )

let runDemoClientScenario scenario =
    task {
        let runProcess (fileName: string) (arguments: string list) =
            task {
                let startInfo = ProcessStartInfo()
                startInfo.FileName <- fileName

                for arg in arguments do
                    startInfo.ArgumentList.Add(arg)

                startInfo.WorkingDirectory <- repoRoot ()
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.UseShellExecute <- false

                use proc = new Process()
                proc.StartInfo <- startInfo

                if not (proc.Start()) then
                    invalidOp $"Failed to start process '{fileName}'."

                let! stdout = proc.StandardOutput.ReadToEndAsync()
                let! stderr = proc.StandardError.ReadToEndAsync()
                do! proc.WaitForExitAsync()

                return proc.ExitCode, stdout, stderr
            }

        let! buildExitCode, buildStdout, buildStderr =
            runProcess "dotnet" [ "build"; demoClientProjectPath (); "--no-restore"; "-m:1" ]

        if buildExitCode <> 0 then
            return buildExitCode, buildStdout, buildStderr
        else
            return! runProcess "dotnet" [ demoClientDllPath (); scenario ]
    }
