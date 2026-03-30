namespace FSharp.MCP.DevKit.Server

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Linq
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.ControlPlane

type McpServerLaunchSpec =
    { Command: string
      Arguments: string list
      WorkingDirectory: string
      EnvironmentVariables: IDictionary<string, string> }

type McpClientConnectionOptions =
    { LaunchSpec: McpServerLaunchSpec option
      ClientOptions: McpClientOptions option
      LoggerFactory: ILoggerFactory option
      CancellationToken: CancellationToken }

type McpClientSession internal (client: McpClient, stderrLog: ConcurrentQueue<string>) =
    let readToolText (result: CallToolResult) =
        result.Content
        |> Seq.choose (function
            | :? TextContentBlock as block -> Some block.Text
            | _ -> None)
        |> String.concat "\n"

    let readResourceText (result: ReadResourceResult) =
        result.Contents
        |> Seq.choose (function
            | :? TextResourceContents as content -> Some content.Text
            | _ -> None)
        |> String.concat "\n"

    member _.Client = client

    member _.StderrLog = stderrLog.ToArray() |> Array.toList

    member _.PingAsync(?cancellationToken: CancellationToken) =
        client.PingAsync(cancellationToken = defaultArg cancellationToken CancellationToken.None).AsTask()

    member _.ListToolNamesAsync(?cancellationToken: CancellationToken) =
        task {
            let! tools = client.ListToolsAsync(cancellationToken = defaultArg cancellationToken CancellationToken.None).AsTask()
            return tools |> Seq.map (fun tool -> tool.Name) |> Seq.toList
        }

    member _.ListResourceUrisAsync(?cancellationToken: CancellationToken) =
        task {
            let! resources = client.ListResourcesAsync(cancellationToken = defaultArg cancellationToken CancellationToken.None).AsTask()
            return resources |> Seq.map (fun resource -> resource.Uri) |> Seq.toList
        }

    member _.ListResourceTemplateUrisAsync(?cancellationToken: CancellationToken) =
        task {
            let! templates = client.ListResourceTemplatesAsync(cancellationToken = defaultArg cancellationToken CancellationToken.None).AsTask()
            return templates |> Seq.map (fun template -> template.UriTemplate) |> Seq.toList
        }

    member _.CallToolAsync(toolName: string, ?arguments: IReadOnlyDictionary<string, obj>, ?cancellationToken: CancellationToken) =
        (client.CallToolAsync(
            toolName,
            arguments = defaultArg arguments null,
            cancellationToken = defaultArg cancellationToken CancellationToken.None
        ))
            .AsTask()

    member this.CallToolTextAsync(toolName: string, ?arguments: IReadOnlyDictionary<string, obj>, ?cancellationToken: CancellationToken) =
        task {
            let! result = this.CallToolAsync(toolName, ?arguments = arguments, ?cancellationToken = cancellationToken)
            return readToolText result
        }

    member this.CallToolJsonAsync<'T>(toolName: string, ?arguments: IReadOnlyDictionary<string, obj>, ?cancellationToken: CancellationToken) =
        task {
            let! json = this.CallToolTextAsync(toolName, ?arguments = arguments, ?cancellationToken = cancellationToken)
            try
                return FSharpJson.deserialize<'T> json
            with ex ->
                let stderr = String.concat "\n" this.StderrLog
                return
                    raise (
                        JsonException(
                            $"Failed to deserialize tool response for '{toolName}'. Raw response: {json}\nSTDERR:\n{stderr}",
                            ex
                        )
                    )
        }

    member this.EnsureRouteAsync
        (
            agentId: string,
            ?displayName: string,
            ?hostId: string,
            ?sessionId: string,
            ?sessionName: string,
            ?cancellationToken: CancellationToken
        ) =
        let pairs = ResizeArray<string * obj>()
        pairs.Add(("agentId", box agentId))
        pairs.Add(("displayName", box (defaultArg displayName "")))
        pairs.Add(("hostId", box (defaultArg hostId "")))
        pairs.Add(("sessionId", box (defaultArg sessionId "")))
        pairs.Add(("sessionName", box (defaultArg sessionName "")))

        let dictionary = Dictionary<string, obj>()

        for key, value in pairs do
            dictionary.[key] <- value

        this.CallToolJsonAsync<EnsureRouteResponse>(
            "ensure_fsi_route",
            (dictionary :> IReadOnlyDictionary<string, obj>),
            ?cancellationToken = cancellationToken
        )

    member _.ReadResourceAsync(uri: string, ?cancellationToken: CancellationToken) =
        client.ReadResourceAsync(uri, cancellationToken = defaultArg cancellationToken CancellationToken.None).AsTask()

    member this.ReadResourceTextAsync(uri: string, ?cancellationToken: CancellationToken) =
        task {
            let! result = this.ReadResourceAsync(uri, ?cancellationToken = cancellationToken)
            return readResourceText result
        }

    member this.ReadResourceJsonAsync<'T>(uri: string, ?cancellationToken: CancellationToken) =
        task {
            let! json = this.ReadResourceTextAsync(uri, ?cancellationToken = cancellationToken)
            try
                return FSharpJson.deserialize<'T> json
            with ex ->
                let stderr = String.concat "\n" this.StderrLog
                return
                    raise (
                        JsonException(
                            $"Failed to deserialize resource '{uri}'. Raw response: {json}\nSTDERR:\n{stderr}",
                            ex
                        )
                    )
        }

    member this.WaitForAsyncStatusAsync
        (
            asyncId: string,
            ?maxAttempts: int,
            ?pollIntervalMs: int,
            ?cancellationToken: CancellationToken
        ) =
        let effectiveCancellationToken = defaultArg cancellationToken CancellationToken.None
        let effectiveMaxAttempts = defaultArg maxAttempts 60
        let effectivePollIntervalMs = defaultArg pollIntervalMs 100

        let rec poll attempt =
            task {
                let! status =
                    this.ReadResourceJsonAsync<AsyncFsiStatusDto>(
                        $"fsi/async/{asyncId}",
                        cancellationToken = effectiveCancellationToken
                    )

                if status.Exists && status.IsCompleted then
                    return status
                elif attempt >= effectiveMaxAttempts then
                    let stderr = String.concat "\n" this.StderrLog
                    return raise (TimeoutException($"Timed out waiting for async status '{asyncId}'. STDERR:\n{stderr}"))
                else
                    do! Task.Delay(effectivePollIntervalMs, effectiveCancellationToken)
                    return! poll (attempt + 1)
            }

        poll 0

    interface IAsyncDisposable with
        member _.DisposeAsync() = client.DisposeAsync()

[<RequireQualifiedAccess>]
module McpClientHarness =
    let private defaultCommand = "dotnet"
    let private defaultFramework = "net10.0"
    let private defaultConfiguration = "Debug"
    let private defaultServerAssemblyName = "FSharp.MCP.DevKit.dll"

    let private toDictionary (pairs: seq<string * string>) =
        let dictionary = Dictionary<string, string>()

        for key, value in pairs do
            dictionary.[key] <- value

        dictionary :> IDictionary<string, string>

    let arguments (pairs: seq<string * obj>) =
        let dictionary = Dictionary<string, obj>()

        for key, value in pairs do
            dictionary.[key] <- value

        dictionary :> IReadOnlyDictionary<string, obj>

    let rec private tryFindRepoRoot (directory: DirectoryInfo) =
        let markerPath = Path.Combine(directory.FullName, "src", "FSharp.MCP.DevKit.Server", "FSharp.MCP.DevKit.Server.fsproj")

        if File.Exists(markerPath) then
            Some directory.FullName
        else
            match directory.Parent with
            | null -> None
            | parent -> tryFindRepoRoot parent

    let resolveRepositoryRoot (startDirectory: string option) =
        let initialDirectory =
            defaultArg startDirectory AppContext.BaseDirectory
            |> DirectoryInfo

        match tryFindRepoRoot initialDirectory with
        | Some repoRoot -> repoRoot
        | None -> invalidOp $"Unable to locate FSharp.MCP.DevKit repository root starting from '{initialDirectory.FullName}'."

    let resolveServerDllPath (repoRoot: string option) (configuration: string option) (framework: string option) =
        let root = defaultArg repoRoot (resolveRepositoryRoot None)
        let resolvedConfiguration = defaultArg configuration defaultConfiguration
        let resolvedFramework = defaultArg framework defaultFramework

        let candidate =
            Path.Combine(
                root,
                "src",
                "FSharp.MCP.DevKit.Server",
                "bin",
                resolvedConfiguration,
                resolvedFramework,
                defaultServerAssemblyName
            )

        if File.Exists(candidate) then
            candidate
        else
            invalidOp $"Server DLL was not found at '{candidate}'. Build the server project first."

    let createStdioLaunchSpec
        (serverDllPath: string option)
        (repoRoot: string option)
        (configuration: string option)
        (framework: string option)
        (additionalArguments: string list option)
        (environmentVariables: seq<string * string> option)
        (workingDirectory: string option) =
        let resolvedServerDllPath =
            match serverDllPath with
            | Some explicitPath -> explicitPath
            | None -> resolveServerDllPath repoRoot configuration framework

        let baseEnvironment =
            [ "MCP_ENABLE_STDIO", "true"
              "ASPNETCORE_URLS", "http://127.0.0.1:0" ]

        let mergedEnvironment =
            [ yield! baseEnvironment
              yield! defaultArg environmentVariables Seq.empty ]
            |> Seq.groupBy fst
            |> Seq.map (fun (key, values) -> key, values |> Seq.last |> snd)
            |> toDictionary

        { Command = defaultCommand
          Arguments = [ "exec"; resolvedServerDllPath ] @ (defaultArg additionalArguments [])
          WorkingDirectory =
            defaultArg workingDirectory (Path.GetDirectoryName(resolvedServerDllPath))
          EnvironmentVariables = mergedEnvironment }

    let defaultConnectionOptions =
        { LaunchSpec = None
          ClientOptions = None
          LoggerFactory = None
          CancellationToken = CancellationToken.None }

    let createDefaultStdioLaunchSpec () =
        createStdioLaunchSpec None None None None None None None

    let createStdioClientAsyncWithOptions (options: McpClientConnectionOptions) =
        task {
            let spec =
                defaultArg options.LaunchSpec (createDefaultStdioLaunchSpec ())

            let stderrLog = ConcurrentQueue<string>()
            let transportOptions =
                StdioClientTransportOptions(
                    Command = spec.Command,
                    Arguments = (ResizeArray(spec.Arguments) :> IList<string>),
                    WorkingDirectory = spec.WorkingDirectory,
                    EnvironmentVariables = spec.EnvironmentVariables,
                    StandardErrorLines = Action<string>(fun line -> stderrLog.Enqueue(line))
                )

            let transport = StdioClientTransport(transportOptions, defaultArg options.LoggerFactory null)
            let! client =
                McpClient.CreateAsync(
                    transport,
                    clientOptions = defaultArg options.ClientOptions null,
                    loggerFactory = defaultArg options.LoggerFactory null,
                    cancellationToken = options.CancellationToken
                )

            return McpClientSession(client, stderrLog)
        }

    let createStdioClientAsync () = createStdioClientAsyncWithOptions defaultConnectionOptions

    let createHttpClientAsync
        (endpoint: Uri)
        (additionalHeaders: seq<string * string>)
        (clientOptions: McpClientOptions option)
        (loggerFactory: ILoggerFactory option)
        (cancellationToken: CancellationToken) =
        task {
            let transportOptions = HttpClientTransportOptions(Endpoint = endpoint)

            match Seq.isEmpty additionalHeaders with
            | false ->
                let dictionary = Dictionary<string, string>()

                for key, value in additionalHeaders do
                    dictionary.[key] <- value

                transportOptions.AdditionalHeaders <- dictionary
            | true -> ()

            let transport = HttpClientTransport(transportOptions, defaultArg loggerFactory null)
            let! client =
                McpClient.CreateAsync(
                    transport,
                    clientOptions = defaultArg clientOptions null,
                    loggerFactory = defaultArg loggerFactory null,
                    cancellationToken = cancellationToken
                )

            return McpClientSession(client, ConcurrentQueue<string>())
        }
