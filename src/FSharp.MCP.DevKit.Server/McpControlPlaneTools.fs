namespace FSharp.MCP.DevKit.Server

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Text
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Messages
open FSharp.MCP.DevKit.Server.Integration
open FSharp.MCP.DevKit.Server.ControlPlane
open FSharp.MCP.DevKit.Server.McpFsiTools
open ModelContextProtocol.Server

[<McpServerToolType>]
type McpControlPlaneTools =

    static member private parseHostKind(hostKind: string) =
        match hostKind.Trim().ToLowerInvariant() with
        | "netfx" -> NetFxHost
        | "net10" -> Net10Host
        | "inproc" -> invalidOp "create_fsi_host does not support inproc. Use net10 or netfx for out-of-process hosts."
        | _ -> invalidOp $"Unsupported host kind '{hostKind}'. Valid values: netfx, net10."

    static member private parseHostStatus(hostStatus: string) =
        if String.IsNullOrWhiteSpace hostStatus then
            Ready
        else
            match hostStatus.Trim().ToLowerInvariant() with
            | "creating"
            | "starting" -> Creating
            | "ready"
            | "running" -> Ready
            | "busy" -> Busy
            | "degraded" -> Degraded
            | "stopped" -> Stopped
            | "faulted"
            | "failed" -> Faulted
            | _ -> invalidOp $"Unsupported host status '{hostStatus}'."

    static member private parseArguments(arguments: string option) =
        arguments
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> Option.map (fun value ->
            let tokens = ResizeArray<string>()
            let current = StringBuilder()
            let mutable inSingle = false
            let mutable inDouble = false
            let mutable escape = false

            let flushToken () =
                if current.Length > 0 then
                    tokens.Add(current.ToString())
                    current.Clear() |> ignore

            let mutable index = 0

            while index < value.Length do
                let ch = value.[index]

                if escape then
                    current.Append(ch) |> ignore
                    escape <- false
                elif
                    ch = '\\'
                    && not inSingle
                    && index + 1 < value.Length
                    && (value.[index + 1] = '"' || value.[index + 1] = '\'' || Char.IsWhiteSpace value.[index + 1])
                then
                    escape <- true
                elif ch = '"' && not inSingle then
                    inDouble <- not inDouble
                elif ch = '\'' && not inDouble then
                    inSingle <- not inSingle
                elif Char.IsWhiteSpace ch && not inSingle && not inDouble then
                    flushToken ()
                else
                    current.Append(ch) |> ignore

                index <- index + 1

            if escape then
                current.Append('\\') |> ignore

            flushToken ()
            tokens |> Seq.toList)
        |> Option.defaultValue []

    [<McpServerTool(Name = "register_fsi_agent"); Description("Register or update an agent id for explicit routed FSI usage.")>]
    static member RegisterFsiAgent
        (
            fsiService: FsiMcpService,
            [<Description("Agent identifier used for routed execution and host ownership.")>] agentId: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional display name for the agent.")>] displayName: string
        ) : string =
        let displayNameOpt = if String.IsNullOrWhiteSpace displayName then None else Some displayName
        let record = fsiService.RegisterAgent(agentId, ?displayName = displayNameOpt)
        FSharpJson.serialize record

    [<McpServerTool(Name = "create_fsi_host"); Description("Create an out-of-proc FSI host. Only netfx and net10 are supported, and provisioning always goes through ProcSupervisor. For net10 procnode hosts, the arguments usually need to reference paths visible inside the deployed fsharp-devkit container or host process, not caller-local container paths.")>]
    static member CreateFsiHost
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id. Register the agent first for explicit routing.")>] agentId: string,
            [<Description("Host kind: netfx or net10.")>] hostKind: string,
            [<Description("Executable path for the host process, for example 'dotnet' or a netfx host executable path.")>] executablePath: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Arguments passed to the host process as a single string. Leave empty for none. For net10 procnode hosts, these usually include dotnet exec --runtimeconfig ... --depsfile ... /app/Akka.Proc.Supervisor.dll --mode procnode ... and must use paths visible inside the deployed fsharp-devkit container or host process.")>] arguments: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional working directory for the host process.")>] workingDirectory: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional requested host id. If omitted, a generated id is used.")>] hostId: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional probe message used by ProcSupervisor for active health checks. Leave empty to disable probing. This is not required for host creation; use it after host/session basics are already working.")>] probeMessage: string,
            [<Optional; DefaultParameterValue(0)>]
            [<Description("Optional probe interval in milliseconds. Use 0 to disable probing. This is not required for host creation; use it after host/session basics are already working.")>] probeIntervalMs: int
        ) : Task<string> =
        task {
            let parsedHostKind = McpControlPlaneTools.parseHostKind hostKind

            let argumentsOpt = if String.IsNullOrWhiteSpace arguments then None else Some arguments
            let workingDirectoryOpt = if String.IsNullOrWhiteSpace workingDirectory then None else Some workingDirectory
            let hostIdOpt = if String.IsNullOrWhiteSpace hostId then None else Some hostId
            let probeMessageOpt = if String.IsNullOrWhiteSpace probeMessage then None else Some probeMessage
            let probeIntervalMsOpt = if probeIntervalMs > 0 then Some probeIntervalMs else None

            let args = McpControlPlaneTools.parseArguments argumentsOpt

            let spec =
                { ExecutablePath = executablePath
                  Arguments = args
                  WorkingDirectory = workingDirectoryOpt
                  Role = None
                  ProbeMessage = probeMessageOpt
                  ProbeCron = None
                  ProbeIntervalMs = probeIntervalMsOpt }

            let! hostRecord = fsiService.CreateHost(agentId, parsedHostKind, spec, ?requestedHostId = hostIdOpt)
            return FSharpJson.serialize hostRecord
        }

    [<McpServerTool(Name = "list_fsi_hosts"); Description("List FSI hosts owned by an agent.")>]
    static member ListFsiHosts
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string
        ) : string =
        fsiService.ListHosts(agentId) |> FSharpJson.serialize

    [<McpServerTool(Name = "list_all_fsi_hosts"); Description("List all FSI hosts known by this DevKit control plane, regardless of owning agent.")>]
    static member ListAllFsiHosts(fsiService: FsiMcpService) : string =
        fsiService.ListAllHosts() |> FSharpJson.serialize

    [<McpServerTool(Name = "create_fsi_session"); Description("Create or hydrate a session under an existing host. If later execution faults, create a fresh session before assuming the host itself is broken.")>]
    static member CreateFsiSession
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional session id. If omitted, a generated id is used.")>] sessionId: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional display name for the session.")>] sessionName: string
        ) : Task<string> =
        task {
            let sessionIdOpt = if String.IsNullOrWhiteSpace sessionId then None else Some sessionId
            let sessionNameOpt = if String.IsNullOrWhiteSpace sessionName then None else Some sessionName
            let! sessionRecord = fsiService.CreateSession(agentId, hostId, ?sessionId = sessionIdOpt, ?sessionName = sessionNameOpt)
            return FSharpJson.serialize sessionRecord
        }

    [<McpServerTool(Name = "ensure_fsi_route"); Description("Register an agent and ensure that an agentId/hostId/sessionId route exists before routed execution. This tool is intended for onboarding the legacy default route or an already-provisioned host. To create an out-of-proc host, call create_fsi_host first.")>]
    static member EnsureFsiRoute
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Display name for the agent. Use an empty string to keep the current value.")>] displayName: string,
            [<Description("Host id to ensure. Use an empty string to default to the agent defaultHostId or '<agentId>-host'.")>] hostId: string,
            [<Description("Session id to ensure. Use an empty string for default-session.")>] sessionId: string,
            [<Description("Session display name. Use an empty string to omit.")>] sessionName: string
        ) : Task<string> =
        task {
            let displayNameOpt = if String.IsNullOrWhiteSpace displayName then None else Some displayName
            let hostIdOpt = if String.IsNullOrWhiteSpace hostId then None else Some hostId
            let sessionIdOpt = if String.IsNullOrWhiteSpace sessionId then None else Some sessionId
            let sessionNameOpt = if String.IsNullOrWhiteSpace sessionName then None else Some sessionName

            let! result =
                fsiService.EnsureRoute(
                    agentId,
                    ?displayName = displayNameOpt,
                    ?hostId = hostIdOpt,
                    ?sessionId = sessionIdOpt,
                    ?sessionName = sessionNameOpt
                )

            return FSharpJson.serialize result
        }

    [<McpServerTool(Name = "list_fsi_sessions"); Description("List sessions under a host.")>]
    static member ListFsiSessions
        (
            fsiService: FsiMcpService,
            [<Description("Target host id.")>] hostId: string
        ) : string =
        fsiService.ListHostSessions(hostId) |> FSharpJson.serialize

    [<McpServerTool(Name = "list_all_fsi_sessions"); Description("List all FSI sessions known by this DevKit control plane, regardless of owning agent or host.")>]
    static member ListAllFsiSessions(fsiService: FsiMcpService) : string =
        fsiService.ListAllSessions() |> FSharpJson.serialize

    [<McpServerTool(Name = "register_external_fsi_session"); Description("Register or refresh an already-running external FSI host/session pair so Mgmt2, Codex, and other agents share the same inventory fabric. This does not start a process.")>]
    static member RegisterExternalFsiSession
        (
            fsiService: FsiMcpService,
            [<Description("Agent id that observed or owns the registration.")>] agentId: string,
            [<Description("External host id, usually the procnode/proc id shown by ProcSupervisor.")>] hostId: string,
            [<Description("External session id under the host.")>] sessionId: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional host address or FSI supervisor actor path.")>] hostAddress: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional display name for the session. Defaults to sessionId.")>] sessionName: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional host status, e.g. ready, running, degraded, stopped, faulted.")>] hostStatus: string
        ) : string =
        let hostAddressOpt = if String.IsNullOrWhiteSpace hostAddress then None else Some hostAddress
        let sessionNameOpt = if String.IsNullOrWhiteSpace sessionName then None else Some sessionName
        let parsedStatus = McpControlPlaneTools.parseHostStatus hostStatus

        fsiService.RegisterExternalFsiSession(
            agentId,
            hostId,
            sessionId,
            ?hostAddress = hostAddressOpt,
            ?sessionName = sessionNameOpt,
            hostStatus = parsedStatus
        )
        |> FSharpJson.serialize

    [<McpServerTool(Name = "probe_fsi_host_sessions_liveness"); Description("Force-refresh liveness for all sessions under a host by bypassing the current liveness cache once.")>]
    static member ProbeFsiHostSessionsLiveness
        (
            fsiService: FsiMcpService,
            [<Description("Target host id.")>] hostId: string
        ) : Task<string> =
        task {
            let! payload = fsiService.ProbeHostSessionLiveness(hostId)
            return FSharpJson.serialize payload
        }

    [<McpServerTool(Name = "sweep_fsi_sessions_liveness"); Description("Force-refresh liveness across all registered hosts, or a single host when hostId is provided. This is a sweep-style entry point for schedulers and agent runtimes.")>]
    static member SweepFsiSessionsLiveness
        (
            fsiService: FsiMcpService,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional host id filter. Leave empty to sweep every registered host.")>] hostId: string
        ) : Task<string> =
        task {
            let hostIdOpt = if String.IsNullOrWhiteSpace hostId then None else Some hostId
            let! payload = fsiService.SweepSessionLiveness(?hostId = hostIdOpt)
            return FSharpJson.serialize payload
        }

    [<McpServerTool(Name = "get_fsi_host_health"); Description("Get health information for a host.")>]
    static member GetFsiHostHealth
        (
            fsiService: FsiMcpService,
            [<Description("Target host id.")>] hostId: string
        ) : Task<string> =
        task {
            let! health = fsiService.GetHostHealth(hostId)
            return FSharpJson.serialize health
        }

    [<McpServerTool(Name = "get_fsi_path_mappings"); Description("List known path mappings. If no filters are provided, returns all mappings.")>]
    static member GetFsiPathMappings
        (
            fsiService: FsiMcpService,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional agent id filter.")>] agentId: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional host id filter.")>] hostId: string
        ) : string =
        let agentIdOpt = if String.IsNullOrWhiteSpace agentId then None else Some agentId
        let hostIdOpt = if String.IsNullOrWhiteSpace hostId then None else Some hostId

        fsiService.ListPathMappings(?agentId = agentIdOpt, ?hostId = hostIdOpt)
        |> FSharpJson.serialize

    [<McpServerTool(Name = "register_browser_inventory"); Description("Register or update a SharpBrowser inventory record. Pass a serialized BrowserInventoryDto JSON payload.")>]
    static member RegisterBrowserInventory
        (
            fsiService: FsiMcpService,
            [<Description("Serialized BrowserInventoryDto JSON payload.")>] browserInventoryJson: string
        ) : string =
        let browser = FSharpJson.deserialize<BrowserInventoryDto> browserInventoryJson
        fsiService.UpsertBrowserInventory(browser) |> FSharpJson.serialize

    [<McpServerTool(Name = "list_browser_inventory"); Description("List registered SharpBrowser inventory records, optionally filtered by status or tag.")>]
    static member ListBrowserInventory
        (
            fsiService: FsiMcpService,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional browser status filter, for example ready, offline, or unknown.")>] status: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Optional tag filter, for example remote or sharpbrowser.")>] tag: string,
            [<Optional; DefaultParameterValue(0)>]
            [<Description("Optional positive result limit. Use 0 for no explicit limit.")>] limit: int
        ) : string =
        let statusOpt = if String.IsNullOrWhiteSpace status then None else Some status
        let tagOpt = if String.IsNullOrWhiteSpace tag then None else Some tag
        let limitOpt = if limit > 0 then Some limit else None

        fsiService.ListBrowserInventory(?status = statusOpt, ?tag = tagOpt, ?limit = limitOpt)
        |> FSharpJson.serialize

    [<McpServerTool(Name = "get_browser_inventory"); Description("Read a registered SharpBrowser inventory record by browser id.")>]
    static member GetBrowserInventory
        (
            fsiService: FsiMcpService,
            [<Description("Browser id.")>] browserId: string
        ) : string =
        fsiService.TryGetBrowserInventory(browserId) |> FSharpJson.serialize

    [<McpServerTool(Name = "remove_browser_inventory"); Description("Remove a registered SharpBrowser inventory record by browser id.")>]
    static member RemoveBrowserInventory
        (
            fsiService: FsiMcpService,
            [<Description("Browser id.")>] browserId: string
        ) : string =
        fsiService.RemoveBrowserInventory(browserId) |> FSharpJson.serialize
