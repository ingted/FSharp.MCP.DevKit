module McpSurfaceTests

open System
open System.IO
open System.Reflection
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open ModelContextProtocol.Server
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Server

let private waitForCompletion (service: FsiMcpService) asyncId =
    task {
        let mutable attempt = 0
        let mutable status = service.GetAsyncExecutionStatus(asyncId)

        while not status.IsCompleted && attempt < 50 do
            do! Task.Delay(100)
            attempt <- attempt + 1
            status <- service.GetAsyncExecutionStatus(asyncId)

        return status
    }

let private createTempFsx (content: string) =
    let dir =
        Path.Combine(Path.GetTempPath(), "FSharp.MCP.DevKit.McpSurfaceTests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore
    let filePath = Path.Combine(dir, "scratch.fsx")
    File.WriteAllText(filePath, content, Encoding.UTF8)

    let cleanup =
        { new IDisposable with
            member _.Dispose() =
                if Directory.Exists(dir) then
                    try
                        Directory.Delete(dir, true)
                    with _ ->
                        () }

    filePath, cleanup

let private mcpToolTypes =
    [ typeof<FSharpInteractiveTools>
      typeof<CodeInjectionTools>
      typeof<KillMCPServer>
      typeof<McpControlPlaneTools>
      typeof<McpExecutionTools>
      typeof<McpResultTools>
      typeof<McpDocumentationTools.DocumentationMcpTools> ]

[<Fact>]
let ``MCP tool surface does not expose FSharpOption parameters`` () =
    let fsharpOptionDefinition = typedefof<option<_>>

    let offenders =
        mcpToolTypes
        |> List.collect (fun toolType ->
            toolType.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
            |> Array.filter (fun methodInfo ->
                methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, true).Length > 0)
            |> Array.collect (fun methodInfo ->
                methodInfo.GetParameters()
                |> Array.choose (fun parameter ->
                    if
                        parameter.ParameterType.IsGenericType
                        && parameter.ParameterType.GetGenericTypeDefinition() = fsharpOptionDefinition
                    then
                        Some $"{toolType.FullName}.{methodInfo.Name}:{parameter.Name}:{parameter.ParameterType.FullName}"
                    else
                        None))
            |> Array.toList)

    Assert.True(List.isEmpty offenders, String.concat "\n" offenders)

[<Fact>]
let ``FSharpInteractiveTools execute evaluate add-path and state use routed service`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! execResult = FSharpInteractiveTools.ExecuteFSharpCode(service, "let toolValue = 31", 30)
        let! evalResult = FSharpInteractiveTools.EvaluateFSharpExpression(service, "toolValue", 30)
        let searchPath =
            Path.Combine(Path.GetTempPath(), "FSharp.MCP.DevKit.McpSurfaceTests", Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(searchPath) |> ignore

        use _searchPathCleanup =
            { new IDisposable with
                member _.Dispose() =
                    if Directory.Exists(searchPath) then
                        try
                            Directory.Delete(searchPath, true)
                        with _ ->
                            () }

        let! addPathResult = FSharpInteractiveTools.AddSearchPath(service, searchPath, 30)
        let! stateResult = FSharpInteractiveTools.GetFSIState(service, 30)

        Assert.Contains("toolValue", execResult)
        Assert.Equal("31", evalResult)
        Assert.Equal(sprintf "Search path added successfully: %s" searchPath, addPathResult)
        Assert.Contains("FSI Session State", stateResult)
    }

[<Fact>]
let ``FSharpInteractiveTools detailed error includes routed execution metadata`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! detail = FSharpInteractiveTools.ExecuteFSharpCodeDetailed(service, "missingValue", 30)

        Assert.Contains("=== EXECUTION FAILED ===", detail)
        Assert.Contains("BackendKind: InProc", detail)
        Assert.Contains("SessionId: default-session", detail)
    }

[<Fact>]
let ``Fsi async status resource reflects async tool completion`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! asyncId = FSharpInteractiveTools.ExecuteFSharpCodeAsync(service, "let resourceAsyncValue = 44", 30)
        let! _ = waitForCompletion service asyncId
        let resource = FSharp.MCP.DevKit.Server.Program.FsiResources(service)
        let json = resource.AsyncStatus(asyncId)
        let status = FSharpJson.deserialize<AsyncFsiStatusDto> json

        Assert.Equal(asyncId, status.AsyncId)
        Assert.True(status.Exists)
        Assert.True(status.IsCompleted)
        Assert.True(status.ResultId.IsSome)
        Assert.Equal(Some "default-agent", status.AgentId)
        Assert.Equal(Some "default-host", status.HostId)
        Assert.Equal(Some "default-session", status.SessionId)
        Assert.True(status.Result.IsSome)
    }

[<Fact>]
let ``get_async_status tool matches async resource status`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! asyncId = FSharpInteractiveTools.ExecuteFSharpCodeAsync(service, "let toolAsyncValue = 55", 30)
        let! _ = waitForCompletion service asyncId
        let resource = FSharp.MCP.DevKit.Server.Program.FsiResources(service)
        let resourceJson = resource.AsyncStatus(asyncId)
        let! toolJson = FSharpInteractiveTools.GetAsyncStatus(service, asyncId)
        let resourceStatus = FSharpJson.deserialize<AsyncFsiStatusDto> resourceJson
        let toolStatus = FSharpJson.deserialize<AsyncFsiStatusDto> toolJson

        Assert.Equal(resourceStatus.AsyncId, toolStatus.AsyncId)
        Assert.Equal(resourceStatus.Exists, toolStatus.Exists)
        Assert.Equal(resourceStatus.IsCompleted, toolStatus.IsCompleted)
        Assert.Equal(resourceStatus.ResultId, toolStatus.ResultId)
        Assert.Equal(resourceStatus.AgentId, toolStatus.AgentId)
        Assert.Equal(resourceStatus.HostId, toolStatus.HostId)
        Assert.Equal(resourceStatus.SessionId, toolStatus.SessionId)
    }

[<Fact>]
let ``ParseAndCheckFSharpCode uses static analysis without timing out on small valid source`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let source =
            "module Scratch\n\nlet add x y = x + y\nlet answer = add 1 2"

        let! result = FSharpInteractiveTools.ParseAndCheckFSharpCode(service, source, 5)

        Assert.Contains("Static parse/check completed", result)
        Assert.Contains("Success: true", result)
        Assert.DoesNotContain("Timeout", result)
    }

[<Fact>]
let ``ParseAndCheckFSharpCode reports diagnostics for invalid source without timing out`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let! result = FSharpInteractiveTools.ParseAndCheckFSharpCode(service, "let broken =", 5)

        Assert.Contains("Static parse/check completed", result)
        Assert.Contains("Diagnostics:", result)
        Assert.DoesNotContain("Timeout", result)
    }

[<Fact>]
let ``ParseSourceToAST uses static analysis and returns symbol summary`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable

        let source =
            "module Scratch\n\ntype Person = { Name: string }\nlet create name = { Name = name }"

        let! result = CodeInjectionTools.ParseSourceToAST(service, source)

        Assert.Contains("Static AST summary", result)
        Assert.Contains("Symbol count:", result)
        Assert.DoesNotContain("Timeout", result)
    }

[<Fact>]
let ``AnalyzeCodeStructure uses static analysis and returns file summary`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable
        let filePath, tempCleanup = createTempFsx "module Scratch\n\nlet add x y = x + y"
        use _tempCleanup = tempCleanup

        let! result = CodeInjectionTools.AnalyzeCodeStructure(service, filePath)

        Assert.Contains("File:", result)
        Assert.Contains("Symbol count:", result)
        Assert.DoesNotContain("Timeout", result)
    }

[<Fact>]
let ``CodeInjectionTools preview preserves existing file content`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable
        let original = "let a = 1\nlet b = 2\nlet c = 3"
        let filePath, tempCleanup = createTempFsx original
        use _tempCleanup = tempCleanup

        let! preview =
            CodeInjectionTools.PreviewCodeInjection(
                service,
                "let inserted = 99",
                filePath,
                insertAtLine = 2
            )

        Assert.Contains("let a = 1", preview)
        Assert.Contains("let inserted = 99", preview)
        Assert.Contains("let b = 2", preview)
        Assert.Equal(original, File.ReadAllText(filePath))
    }

[<Fact>]
let ``InsertCode inserts into existing file without dropping original content`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable
        let filePath, tempCleanup = createTempFsx "let a = 1\nlet b = 2\nlet c = 3"
        use _tempCleanup = tempCleanup

        let! result =
            KillMCPServer.InsertCode(
                service,
                "let inserted = 99",
                filePath,
                2,
                1,
                shouldFormat = false,
                shouldValidate = false
            )

        let updated = File.ReadAllText(filePath)
        Assert.Contains("Code successfully inserted", result)
        Assert.Equal("let a = 1\nlet inserted = 99\nlet b = 2\nlet c = 3", updated)
    }

[<Fact>]
let ``InsertCode refuses missing file instead of creating destructive empty-file insertion`` () =
    task {
        let service = new FsiMcpService(NullLogger<FsiMcpService>.Instance, enableRemoteClient = false)
        use _cleanup = service :> IDisposable
        let dir =
            Path.Combine(Path.GetTempPath(), "FSharp.MCP.DevKit.McpSurfaceTests", Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(dir) |> ignore
        use _tempCleanup =
            { new IDisposable with
                member _.Dispose() =
                    if Directory.Exists(dir) then
                        Directory.Delete(dir, true) }

        let missingPath = Path.Combine(dir, "missing.fsx")

        let! result =
            KillMCPServer.InsertCode(
                service,
                "// inserted header",
                missingPath,
                1,
                1,
                shouldFormat = false,
                shouldValidate = false
            )

        Assert.Contains("File not found", result)
        Assert.False(File.Exists(missingPath))
    }
