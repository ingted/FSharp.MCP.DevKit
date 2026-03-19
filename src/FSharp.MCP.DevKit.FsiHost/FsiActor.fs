module FSharp.MCP.DevKit.FsiHost.Actors

open System
open Akka.Actor
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Messages

let toRemoteDiagnostic (diagnostic: FSharp.Compiler.Diagnostics.FSharpDiagnostic) : FsiRemoteDiagnostic =
    { FileName = diagnostic.FileName
      StartLine = diagnostic.StartLine
      EndLine = diagnostic.EndLine
      StartColumn = diagnostic.StartColumn
      EndColumn = diagnostic.EndColumn
      Severity = diagnostic.Severity.ToString()
      Message = diagnostic.Message }

let toRemoteResult (result: FsiResult) : FsiRemoteResult =
    { Output = result.Output
      Errors = result.Errors
      IsSuccess = result.IsSuccess
      ExecutionTimeMs = result.ExecutionTime |> Option.map (fun value -> value.TotalMilliseconds)
      Diagnostics = result.Diagnostics |> Array.map toRemoteDiagnostic }

let successResult (output: string) : FsiRemoteResult =
    { Output = output
      Errors = ""
      IsSuccess = true
      ExecutionTimeMs = None
      Diagnostics = [||] }

let failureResult (error: string) : FsiRemoteResult =
    { Output = ""
      Errors = error
      IsSuccess = false
      ExecutionTimeMs = None
      Diagnostics = [||] }

type FsiActor(config: FsiConfig) =
    inherit ActorBase()

    let fsi = new FsiService(config)
    do fsi.Start()

    override this.Receive(message: obj) =
        match message with
        | :? FsiRemoteCommandRequest as req ->
            let result =
                try
                    match req.CommandType with
                    | "EXEC" -> fsi.ExecuteInteraction(req.Payload) |> toRemoteResult
                    | "EVAL" -> fsi.EvaluateExpression(req.Payload) |> toRemoteResult
                    | "LOAD" -> fsi.ExecuteInteraction($"#load \"{req.Payload}\"") |> toRemoteResult
                    | "PARSE" -> fsi.ParseAndCheck(req.Payload) |> toRemoteResult
                    | "REFERENCE_NUGET" ->
                        fsi.ReferenceNugetPackage(req.Payload, ?usePackageTargets = req.UsePackageTargets)
                        |> toRemoteResult
                    | "REFERENCE_ASSEMBLY" -> fsi.ReferenceAssembly(req.Payload) |> toRemoteResult
                    | "ADD_PATH" -> fsi.AddSearchPath(req.Payload) |> toRemoteResult
                    | "RESET" ->
                        fsi.Reset()
                        successResult "FSI session reset"
                    | "RESTART" ->
                        fsi.Restart()
                        successResult "FSI session restarted"
                    | "STATE" ->
                        fsi.GetState()
                        |> successResult
                    | "PING" -> successResult "PONG"
                    | unknown -> failureResult $"Unsupported remote FSI command: {unknown}"
                with ex ->
                    failureResult ex.Message

            let response : FsiRemoteCommandResponse =
                { RequestId = req.RequestId
                  Result = result }

            this.Sender.Tell(response)
            true
        | _ -> false

    static member Props(config: FsiConfig) =
        Props.Create<FsiActor>(config)
