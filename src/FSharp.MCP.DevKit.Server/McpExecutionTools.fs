namespace FSharp.MCP.DevKit.Server

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.McpFsiTools
open FSharp.MCP.DevKit.Server.Backends
open ModelContextProtocol.Server

type private BrowserExecutionResponse =
    { ResultId: string
      RequestId: string
      HostId: string
      SessionId: string
      BrowserId: string
      TabId: string option
      IsSuccess: bool
      Output: string
      Errors: string
      Metadata: Map<string, string> }

[<McpServerToolType>]
type McpExecutionTools =

    static member private route agentId hostId sessionId =
        { AgentId = agentId
          HostId = hostId
          SessionId = sessionId }

    static member private resolveTimeout (fsiService: FsiMcpService) timeoutSeconds =
        if timeoutSeconds > 0 then
            TimeSpan.FromSeconds(float timeoutSeconds)
        else
            fsiService.DefaultTimeout

    static member private optionalValue (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            Some value

    static member private browserScheduleMetadata targetKind browserId tabId companionHostId companionSessionId executionPlane =
        [ "schedule.target.kind", targetKind
          "schedule.target.browserId", browserId
          "schedule.target.tabId", tabId
          "schedule.target.companion.hostId", companionHostId
          "schedule.target.companion.sessionId", companionSessionId
          "schedule.target.executionPlane", executionPlane ]
        |> List.choose (fun (key, value) ->
            if String.IsNullOrWhiteSpace value then
                None
            else
                Some(key, value))
        |> Map.ofList

    static member private formatResultError (fallbackMessage: string) (result: FsiResult) =
        if String.IsNullOrWhiteSpace result.Errors then
            fallbackMessage
        else
            result.Errors

    static member private formatRecordError (fallbackMessage: string) (record: FsiExecutionRecord) =
        let baseError =
            if String.IsNullOrWhiteSpace record.Result.Errors then
                fallbackMessage
            else
                record.Result.Errors

        let context =
            $"[RequestId={record.RequestId}, HostId={record.HostId}, SessionId={record.SessionId}, Backend={record.BackendKind}, ResultId={record.ResultId}]"

        let hint =
            if baseError.Contains("could not be completed due to earlier error")
               || baseError.Contains("Faulted") then
                "\nHint: The session may be in Faulted state. Call reset_fsi_session_routed or create a new session to recover."
            else
                ""

        $"{baseError}\n{context}{hint}"

    [<McpServerTool(Name = "execute_browser_f_sharp_code_routed"); Description("Execute F# code against a browser-aware target. The code is dispatched to the companion FSI session route and the execution record is tagged with schedule.target.* and normalized browser.* metadata.")>]
    static member ExecuteBrowserFSharpCodeRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Companion FSI host id.")>] hostId: string,
            [<Description("Companion FSI session id.")>] sessionId: string,
            [<Description("Browser id to tag on the execution metadata.")>] browserId: string,
            [<Description("F# code to execute in the companion FSI session.")>] code: string,
            [<Optional; DefaultParameterValue("")>]
            [<Description("Browser tab id. Leave blank for browser-level targets.")>] tabId: string,
            [<Optional; DefaultParameterValue("tab")>]
            [<Description("Schedule target kind, e.g. browser, tab, companion-session, or tabs.")>] targetKind: string,
            [<Optional; DefaultParameterValue("")>]
            [<Description("Companion host id to store in metadata. Defaults to hostId.")>] companionHostId: string,
            [<Optional; DefaultParameterValue("")>]
            [<Description("Companion session id to store in metadata. Defaults to sessionId.")>] companionSessionId: string,
            [<Optional; DefaultParameterValue("remote-fsi")>]
            [<Description("Execution plane label stored in metadata.")>] executionPlane: string,
            [<Optional; DefaultParameterValue(0)>]
            [<Description("Timeout in seconds (optional, default: 30).")>] timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds

            let metadata =
                McpExecutionTools.browserScheduleMetadata
                    targetKind
                    browserId
                    tabId
                    (companionHostId |> McpExecutionTools.optionalValue |> Option.defaultValue hostId)
                    (companionSessionId |> McpExecutionTools.optionalValue |> Option.defaultValue sessionId)
                    executionPlane

            let! record =
                fsiService.ExecuteOperation(
                    ExecuteCode,
                    code,
                    timeout = timeout,
                    requestedRoute = route,
                    metadata = metadata
                )

            return
                FSharpJson.serialize
                    { ResultId = record.ResultId
                      RequestId = record.RequestId
                      HostId = record.HostId
                      SessionId = record.SessionId
                      BrowserId = browserId
                      TabId = McpExecutionTools.optionalValue tabId
                      IsSuccess = record.Result.IsSuccess
                      Output = record.Result.Output
                      Errors =
                        if record.Result.IsSuccess then
                            record.Result.Errors
                        else
                            McpExecutionTools.formatRecordError "Browser-aware execution failed" record
                      Metadata = record.Metadata }
        }

    [<McpServerTool(Name = "execute_f_sharp_code_routed"); Description("Execute F# code against an explicit agentId/hostId/sessionId route. Prefer this for short snippets and quick probes. For long-running or heavy scripts, prefer execute_f_sharp_code_async_routed to avoid synchronous ask/disconnection issues on remote hosts.")>]
    static member ExecuteFSharpCodeRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("F# code to execute. If the code includes #I/#r paths, those paths must be visible from the remote host container or process, not just from the caller's container.")>] code: string,
            [<Optional; DefaultParameterValue(0)>]
            [<Description("Timeout in seconds (optional, default: 30).")>] timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(ExecuteCode, code, timeout = timeout, requestedRoute = route)
            return if record.Result.IsSuccess then record.Result.Output else McpExecutionTools.formatRecordError "Execution failed" record
        }

    [<McpServerTool(Name = "execute_f_sharp_code_async_routed"); Description("Enqueue F# code execution against an explicit route and return an async id immediately. This is the preferred path for long-running or heavy remote scripts. Best flow: 1. Call this tool. 2. Poll get_async_status or resource fsi/async/{asyncId}. 3. When completed, use the same host/session for evaluate_f_sharp_expression_routed if you need to read bindings or values.")>]
    static member ExecuteFSharpCodeAsyncRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("F# code to execute asynchronously. After this tool returns asyncId, poll get_async_status or read resource fsi/async/{asyncId} until isCompleted is true. If the code includes #I/#r paths, those paths must be visible from the remote host container or process, not just from the caller's container.")>] code: string,
            [<Optional; DefaultParameterValue(0)>]
            [<Description("Timeout in seconds (optional, default: 30).")>] timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            return fsiService.EnqueueExecuteCode(code, timeout, requestedRoute = route)
        }

    [<McpServerTool(Name = "evaluate_f_sharp_expression_routed"); Description("Evaluate an F# expression against an explicit route. Common flow for long workloads: first run execute_f_sharp_code_async_routed, wait for completion, then evaluate against the same agentId/hostId/sessionId.")>]
    static member EvaluateFSharpExpressionRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("F# expression to evaluate.")>] expression: string,
            [<Optional; DefaultParameterValue(0)>]
            [<Description("Timeout in seconds (optional, default: 30).")>] timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(EvaluateExpression, expression, timeout = timeout, requestedRoute = route)

            return
                if record.Result.IsSuccess then
                    record.Result.Value |> Option.defaultValue record.Result.Output
                else
                    McpExecutionTools.formatRecordError "Expression evaluation failed" record
        }

    [<McpServerTool(Name = "add_search_path_routed"); Description("Add an F# search path against an explicit route. The path must exist from the remote host container or process perspective; caller-local container paths may not work.")>]
    static member AddSearchPathRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("Directory path to add to the F# search path. Use a path visible from the remote host container or process.")>] path: string,
            [<Optional; DefaultParameterValue(0)>]
            [<Description("Timeout in seconds (optional, default: 30).")>] timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(AddSearchPath, path, timeout = timeout, requestedRoute = route)
            return if record.Result.IsSuccess then $"Search path added successfully: {path}" else McpExecutionTools.formatRecordError "Failed to add search path" record
        }

    [<McpServerTool(Name = "reference_assembly_routed"); Description("Reference an assembly against an explicit route. If you pass a file path, it must be visible from the remote host container or process.")>]
    static member ReferenceAssemblyRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Description("Assembly path or assembly name. If you pass a path, it must be visible from the remote host container or process.")>] assemblyPath: string,
            [<Optional; DefaultParameterValue(0)>]
            [<Description("Timeout in seconds (optional, default: 30).")>] timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(ReferenceAssembly, assemblyPath, timeout = timeout, requestedRoute = route)
            return if record.Result.IsSuccess then $"Assembly referenced successfully: {assemblyPath}" else McpExecutionTools.formatRecordError "Failed to reference assembly" record
        }

    [<McpServerTool(Name = "reset_fsi_session_routed"); Description("Reset a specific session under an explicit route.")>]
    static member ResetFsiSessionRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Optional; DefaultParameterValue(0)>]
            [<Description("Timeout in seconds (optional, default: 30).")>] timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(ResetSession, "", timeout = timeout, requestedRoute = route)
            return if record.Result.IsSuccess then "FSI session reset successfully" else McpExecutionTools.formatRecordError "Failed to reset FSI session" record
        }

    [<McpServerTool(Name = "get_fsi_state_routed"); Description("Get FSI state for an explicit agentId/hostId/sessionId route.")>]
    static member GetFsiStateRouted
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Target session id.")>] sessionId: string,
            [<Optional; DefaultParameterValue(0)>]
            [<Description("Timeout in seconds (optional, default: 30).")>] timeoutSeconds: int
        ) : Task<string> =
        task {
            let route = McpExecutionTools.route agentId hostId sessionId
            let timeout = McpExecutionTools.resolveTimeout fsiService timeoutSeconds
            let! record = fsiService.ExecuteOperation(GetState, "", timeout = timeout, requestedRoute = route)
            return if record.Result.IsSuccess then record.Result.Output else McpExecutionTools.formatRecordError "Failed to get FSI state" record
        }
