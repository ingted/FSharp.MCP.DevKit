namespace FSharp.MCP.DevKit.Server

open System
open System.ComponentModel
open System.Text.Json
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
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
        | "inproc" -> invalidOp "create_fsi_host does not support inproc."
        | _ -> invalidOp $"Unsupported host kind '{hostKind}'. Expected netfx or net10."

    [<McpServerTool(Name = "register_fsi_agent"); Description("Register or update an agent id for explicit routed FSI usage.")>]
    static member RegisterFsiAgent
        (
            fsiService: FsiMcpService,
            [<Description("Agent identifier used for routed execution and host ownership.")>] agentId: string,
            [<Description("Optional display name for the agent.")>] ?displayName: string
        ) : string =
        let record = fsiService.RegisterAgent(agentId, ?displayName = displayName)
        JsonSerializer.Serialize(record)

    [<McpServerTool(Name = "create_fsi_host"); Description("Create an out-of-proc FSI host. Only netfx and net10 are supported, and provisioning always goes through ProcSupervisor.")>]
    static member CreateFsiHost
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id. Register the agent first for explicit routing.")>] agentId: string,
            [<Description("Host kind: netfx or net10.")>] hostKind: string,
            [<Description("Executable path for the host process, for example 'dotnet' or a netfx host executable path.")>] executablePath: string,
            [<Description("Arguments passed to the host process as a single string. Leave empty for none.")>] ?arguments: string,
            [<Description("Optional working directory for the host process.")>] ?workingDirectory: string,
            [<Description("Optional requested host id. If omitted, a generated id is used.")>] ?hostId: string,
            [<Description("Optional probe message used by ProcSupervisor.")>] ?probeMessage: string,
            [<Description("Optional probe interval in milliseconds.")>] ?probeIntervalMs: int
        ) : Task<string> =
        task {
            let parsedHostKind = McpControlPlaneTools.parseHostKind hostKind

            let args =
                arguments
                |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                |> Option.map (fun value ->
                    value.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.toList)
                |> Option.defaultValue []

            let spec =
                { ExecutablePath = executablePath
                  Arguments = args
                  WorkingDirectory = workingDirectory
                  Role = Some hostKind
                  ProbeMessage = probeMessage
                  ProbeCron = None
                  ProbeIntervalMs = probeIntervalMs }

            let! hostRecord = fsiService.CreateHost(agentId, parsedHostKind, spec, ?requestedHostId = hostId)
            return JsonSerializer.Serialize(hostRecord)
        }

    [<McpServerTool(Name = "list_fsi_hosts"); Description("List FSI hosts owned by an agent.")>]
    static member ListFsiHosts
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string
        ) : string =
        fsiService.ListHosts(agentId) |> JsonSerializer.Serialize

    [<McpServerTool(Name = "create_fsi_session"); Description("Create or hydrate a session under an existing host.")>]
    static member CreateFsiSession
        (
            fsiService: FsiMcpService,
            [<Description("Owning agent id.")>] agentId: string,
            [<Description("Target host id.")>] hostId: string,
            [<Description("Optional session id. If omitted, a generated id is used.")>] ?sessionId: string,
            [<Description("Optional display name for the session.")>] ?sessionName: string
        ) : Task<string> =
        task {
            let! sessionRecord = fsiService.CreateSession(agentId, hostId, ?sessionId = sessionId, ?sessionName = sessionName)
            return JsonSerializer.Serialize(sessionRecord)
        }

    [<McpServerTool(Name = "list_fsi_sessions"); Description("List sessions under a host.")>]
    static member ListFsiSessions
        (
            fsiService: FsiMcpService,
            [<Description("Target host id.")>] hostId: string
        ) : string =
        fsiService.ListHostSessions(hostId) |> JsonSerializer.Serialize

    [<McpServerTool(Name = "get_fsi_host_health"); Description("Get health information for a host.")>]
    static member GetFsiHostHealth
        (
            fsiService: FsiMcpService,
            [<Description("Target host id.")>] hostId: string
        ) : Task<string> =
        task {
            let! health = fsiService.GetHostHealth(hostId)
            return JsonSerializer.Serialize(health)
        }

    [<McpServerTool(Name = "get_fsi_path_mappings"); Description("List known path mappings. If no filters are provided, returns all mappings.")>]
    static member GetFsiPathMappings
        (
            fsiService: FsiMcpService,
            [<Description("Optional agent id filter.")>] ?agentId: string,
            [<Description("Optional host id filter.")>] ?hostId: string
        ) : string =
        fsiService.ListPathMappings(?agentId = agentId, ?hostId = hostId)
        |> JsonSerializer.Serialize
