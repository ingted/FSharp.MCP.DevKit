namespace FSharp.MCP.DevKit.Server

open System
open System.IO
open System.ComponentModel
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Akka.Actor
open Akka.Configuration
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Core.Actors
open ModelContextProtocol.Server

module McpFsiTools =

    type FsiMcpService(logger: ILogger<FsiMcpService>) =
        let configContent = File.ReadAllText("akka.server.conf")
        let config = ConfigurationFactory.ParseString(configContent)
        let system = ActorSystem.Create("McpClientSystem", config)
        let remoteActorPath = "akka.tcp://FsiExecutionSystem@localhost:8081/user/fsiActor"
        let remoteActor = system.ActorSelection(remoteActorPath)
        
        let mutable defaultTimeout = TimeSpan.FromSeconds(30.0)
        member this.SetDefaultTimeout(timeout: TimeSpan) = defaultTimeout <- timeout
        member this.DefaultTimeout = defaultTimeout

        member this.ExecuteAsync(code: string, isExpression: bool, ?timeout: TimeSpan) =
            let t = defaultArg timeout defaultTimeout
            task {
                try
                    let req = { RequestId = Guid.NewGuid().ToString(); Code = code; IsExpression = isExpression }
                    let! (resp: FsiEvalResponse) = remoteActor.Ask<FsiEvalResponse>(req, t)
                    return resp.Result
                with ex ->
                    logger.LogError(ex, "Failed to communicate with FSI Host actor")
                    return { Output = ""; Errors = ex.Message; IsSuccess = false; Value = None; ExecutionTime = None; Diagnostics = [||] }
            }

        interface IDisposable with
            member _.Dispose() = system.Dispose()

    [<McpServerToolType>]
    type FSharpInteractiveTools =
        [<McpServerTool; Description("Execute F# code in remote .NET FX FSI host")>]
        static member ExecuteFSharpCode(fsiService: FsiMcpService, code: string, ?timeoutSeconds: int) : Task<string> =
            task {
                let timeout = timeoutSeconds |> Option.map (float >> TimeSpan.FromSeconds)
                let! result = fsiService.ExecuteAsync(code, false, ?timeout = timeout)
                if result.IsSuccess then
                    return if String.IsNullOrEmpty(result.Output) then "Success" else result.Output
                else
                    return $"Error: {result.Errors}"
            }