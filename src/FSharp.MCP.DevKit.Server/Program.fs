module FSharp.MCP.DevKit.Server.Program

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Server
open FSharp.MCP.DevKit.Server.McpFsiTools

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


[<EntryPoint>]
let main argv =
    //let builder = Host.CreateApplicationBuilder(argv)
    let builder = WebApplication.CreateBuilder(argv)

    // Configure logging to stderr (required for MCP)
    builder.Logging.AddConsole(fun consoleLogOptions -> consoleLogOptions.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

    let urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    builder.WebHost.UseUrls(if String.IsNullOrWhiteSpace(urls) then "http://0.0.0.0:5000" else urls) |> ignore

    // Register FSI service
    builder.Services.AddSingleton<FsiMcpService>() |> ignore

    // Configure MCP server. Keep stdio enabled by default for local MCP clients,
    // but allow HTTP-only hosting (e.g. container deployment) via MCP_ENABLE_STDIO=false.
    let enableStdio =
        let value = Environment.GetEnvironmentVariable("MCP_ENABLE_STDIO")
        String.IsNullOrWhiteSpace(value)
        || not (
            value.Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase))

    let mcpBuilder =
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly()
            .WithResources<TimeResources>()

    if enableStdio then
        mcpBuilder.WithStdioServerTransport() |> ignore

    let host = builder.Build()
    host.MapMcp("/mcp") |> ignore
    // Run the host
    host.RunAsync().GetAwaiter().GetResult()
    0
