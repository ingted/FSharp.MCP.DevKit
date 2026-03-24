module FSharp.MCP.DevKit.Server.Program

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open System
open System.ComponentModel
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Hosting.WindowsServices
open ModelContextProtocol.Server
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Core

[<McpServerResourceType>]
type TimeResources() =

    // 直接資源（列在 resources/list）
    [<McpServerResource(Name = "worldtime",
                        Title = "World Time (Taipei)",
                        MimeType = "application/json",
                        UriTemplate = "worldtime")>]
    static member WorldTime() =
        let now = DateTime.UtcNow.AddHours(8.0).ToString("yyyy-MM-dd HH:mm:ss")
        // 直接回傳字串 / byte[] / Stream / IEnumerable<string>… SDK會包成 ReadResourceResult
        $"{{\"tz\":\"Asia/Taipei\",\"now\":\"{now}\"}}"

    // 模板資源（列在 resources/templates/list）
    [<McpServerResource(Name = "timeByTz",
                        Title = "Time By Timezone",
                        MimeType = "application/json",
                        // RFC6570 樣式，含參數 → 會被視為模板
                        UriTemplate = "time/{tz}")>]
    static member TimeByTz(tz: string) =
        // 這裡僅示範，不做真正時區換算
        let now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        $"{{\"tz\":\"{tz}\",\"now\":\"{now}\"}}"

[<McpServerResourceType>]
type FsiResources(fsiService: FsiMcpService) =

    [<McpServerResource(
        Name = "fsiAsyncStatus",
        Title = "FSI Async Status",
        MimeType = "application/json",
        UriTemplate = "fsi/async/{asyncId}")>]
    [<Description("Read async FSI execution status by asyncId. Best flow for agents: 1. Call execute_f_sharp_code_async to get asyncId. 2. Read fsi/async/{asyncId}. 3. Poll until isCompleted is true.")>]
    member _.AsyncStatus(asyncId: string) =
        let status = fsiService.GetAsyncExecutionStatus(asyncId)
        FSharpJson.serialize status

let tryGetCommandLineValue (name: string) (argv: string array) =
    argv
    |> Array.tryFindIndex (fun arg -> arg.Equals(name, StringComparison.OrdinalIgnoreCase))
    |> Option.bind (fun index ->
        let valueIndex = index + 1
        if valueIndex < argv.Length then
            Some argv.[valueIndex]
        else
            None)

let getServiceName (argv: string array) =
    let envValue = Environment.GetEnvironmentVariable("DEVKIT_SERVICE_NAME")
    let argValue = tryGetCommandLineValue "--service-name" argv

    [ argValue; if not (String.IsNullOrWhiteSpace(envValue)) then Some envValue ]
    |> List.choose id
    |> List.tryFind (fun value -> not (String.IsNullOrWhiteSpace(value)))
    |> Option.defaultValue "fsharp-devkit"

let getServerUrls (argv: string array) =
    let envValue = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    let argValue = tryGetCommandLineValue "--urls" argv

    [ argValue; if not (String.IsNullOrWhiteSpace(envValue)) then Some envValue ]
    |> List.choose id
    |> List.tryFind (fun value -> not (String.IsNullOrWhiteSpace(value)))
    |> Option.defaultValue "http://0.0.0.0:5000"


[<EntryPoint>]
let main argv =
    //let builder = Host.CreateApplicationBuilder(argv)
    let builder = WebApplication.CreateBuilder(argv)
    let isWindowsService = WindowsServiceHelpers.IsWindowsService()
    let serviceName = getServiceName argv

    builder.Host.UseWindowsService(fun options -> options.ServiceName <- serviceName) |> ignore

    // Configure logging to stderr (required for MCP)
    builder.Logging.AddConsole(fun consoleLogOptions -> consoleLogOptions.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

    builder.WebHost.UseUrls(getServerUrls argv) |> ignore

    let enableRemoteClient =
        let value = Environment.GetEnvironmentVariable("FSI_ENABLE_REMOTE_CLIENT")
        if String.IsNullOrWhiteSpace(value) then
            true
        else
            not (
                value.Equals("0", StringComparison.OrdinalIgnoreCase)
                || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase))

    // Register FSI service
    builder.Services.AddSingleton<FsiMcpService>(fun serviceProvider ->
        let logger = serviceProvider.GetRequiredService<ILogger<FsiMcpService>>()
        new FsiMcpService(logger, enableRemoteClient = enableRemoteClient)
    )
    |> ignore

    // Configure MCP server. Keep stdio enabled by default for local MCP clients,
    // but allow HTTP-only hosting (e.g. container deployment) via MCP_ENABLE_STDIO=false.
    let enableStdio =
        let value = Environment.GetEnvironmentVariable("MCP_ENABLE_STDIO")
        if String.IsNullOrWhiteSpace(value) then
            not isWindowsService
        else
            not (
                value.Equals("0", StringComparison.OrdinalIgnoreCase)
                || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase))

    let mcpBuilder =
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly()
            .WithResources<TimeResources>()
            .WithResources<ControlPlaneResources>()
            .WithResources<ResultResources>()
            .WithResources<FsiResources>()

    if enableStdio then
        mcpBuilder.WithStdioServerTransport() |> ignore

    let host = builder.Build()
    host.MapMcp("/mcp") |> ignore

    host.MapGet(
        "/fsi/async/{asyncId}",
        Func<string, FsiMcpService, IResult>(fun asyncId fsiService ->
            let status = fsiService.GetAsyncExecutionStatus(asyncId)
            Results.Json(status))
    )
    |> ignore

    host.MapGet(
        "/healthz",
        Func<IResult>(fun () ->
            Results.Json(
                {| status = "ok"
                   transport = if enableStdio then "http+stdio-or-http" else "http-only"
                   remoteClient = if enableRemoteClient then "enabled" else "disabled"
                   isWindowsService = isWindowsService
                   serviceName = serviceName |}))
    )
    |> ignore

    // Run the host
    host.RunAsync().GetAwaiter().GetResult()
    0
