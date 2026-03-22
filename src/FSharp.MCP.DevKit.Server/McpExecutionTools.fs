namespace FSharp.MCP.DevKit.Server

open System
open System.ComponentModel
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.McpFsiTools
open ModelContextProtocol.Server

[<McpServerToolType>]
type McpExecutionTools =

    static member private route agentId hostId sessionId =
        { AgentId = agentId
          HostId = hostId
          SessionId = sessionId }

    static member private resolveTimeout (fsiService: FsiMcpService) timeoutSeconds =
        match timeoutSeconds with
        | Some seconds -> TimeSpan.FromSeconds(float seconds)
        | None -> fsiService.DefaultTimeout

    static member private formatResultError (fallbackMessage: string) (result: FsiResult) =
        if String.IsNullOrWhiteSpace result.Errors then
            fallbackMessage
        else
            result.Errors

    [<McpServerTool(Name = "execute_f_sharp_code_routed"); Description("Execute F# code against an explicit agentId/hostId/sessionId route.")>]
    static member ExecuteFSharpCodeRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("F# code to execute.")>] code: string,
            [<Description("Timeout in seconds (optional, default: 30).")>] ?timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(ExecuteCode, code, timeout = timeout, requestedRoute = route)
            return if record.Result.IsSuccess then record.Result.Output else McpExecutionTools.formatResultError "Execution failed" record.Result
        }

    [<McpServerTool(Name = "execute_f_sharp_code_async_routed"); Description("Enqueue F# code execution against an explicit route and return an async id immediately.")>]
    static member ExecuteFSharpCodeAsyncRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("F# code to execute asynchronously.")>] code: string,
            [<Description("Timeout in seconds (optional, default: 30).")>] ?timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            return fsiService.EnqueueExecuteCode(code, timeout, requestedRoute = route)
        }

    [<McpServerTool(Name = "evaluate_f_sharp_expression_routed"); Description("Evaluate an F# expression against an explicit route.")>]
    static member EvaluateFSharpExpressionRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("F# expression to evaluate.")>] expression: string,
            [<Description("Timeout in seconds (optional, default: 30).")>] ?timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(EvaluateExpression, expression, timeout = timeout, requestedRoute = route)

            return
                if record.Result.IsSuccess then
                    record.Result.Value |> Option.defaultValue record.Result.Output
                else
                    McpExecutionTools.formatResultError "Expression evaluation failed" record.Result
        }

    [<McpServerTool(Name = "add_search_path_routed"); Description("Add an F# search path against an explicit route.")>]
    static member AddSearchPathRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("Directory path to add to the F# search path.")>] path: string,
            [<Description("Timeout in seconds (optional, default: 30).")>] ?timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(AddSearchPath, path, timeout = timeout, requestedRoute = route)
            return if record.Result.IsSuccess then $"Search path added successfully: {path}" else McpExecutionTools.formatResultError "Failed to add search path" record.Result
        }

    [<McpServerTool(Name = "reference_assembly_routed"); Description("Reference an assembly against an explicit route.")>]
    static member ReferenceAssemblyRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("Assembly path or assembly name.")>] assemblyPath: string,
            [<Description("Timeout in seconds (optional, default: 30).")>] ?timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(ReferenceAssembly, assemblyPath, timeout = timeout, requestedRoute = route)
            return if record.Result.IsSuccess then $"Assembly referenced successfully: {assemblyPath}" else McpExecutionTools.formatResultError "Failed to reference assembly" record.Result
        }

    [<McpServerTool(Name = "reset_fsi_session_routed"); Description("Reset a specific session under an explicit route.")>]
    static member ResetFsiSessionRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("Timeout in seconds (optional, default: 30).")>] ?timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(ResetSession, "", timeout = timeout, requestedRoute = route)
            return if record.Result.IsSuccess then "FSI session reset successfully" else McpExecutionTools.formatResultError "Failed to reset FSI session" record.Result
        }

    [<McpServerTool(Name = "get_fsi_state_routed"); Description("Get FSI state for an explicit agentId/hostId/sessionId route.")>]
    static member GetFsiStateRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("Timeout in seconds (optional, default: 30).")>] ?timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(GetState, "", timeout = timeout, requestedRoute = route)
            return if record.Result.IsSuccess then record.Result.Output else McpExecutionTools.formatResultError "Failed to get FSI state" record.Result
        }
