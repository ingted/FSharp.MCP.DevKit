namespace FSharp.MCP.DevKit.Server.Backends

open System
open System.Threading.Tasks
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Messages

type RemoteFsiCommand =
    { CommandType: string
      Payload: string
      Route: ExecutionRoute option
      UsePackageTargets: bool option
      Timeout: TimeSpan option }

type IRemoteFsiClient =
    abstract member SendCommand: RemoteFsiCommand -> Task<FsiRemoteCommandResponse>
    abstract member IsServerAvailable: unit -> bool
