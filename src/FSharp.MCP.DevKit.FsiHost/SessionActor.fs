module FSharp.MCP.DevKit.FsiHost.SessionActor

open System
open Akka.Actor
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Messages
open FSharp.MCP.DevKit.FsiHost.ActorHelpers

type SessionActor(sessionId: string, config: FsiConfig) =
    inherit ActorBase()

    let fsi = new FsiService(config)
    let runningSinceUtc = DateTime.UtcNow
    let mutable status = SessionReady
    let mutable refs : string list = []
    let mutable loads : string list = []
    let mutable searchPaths : string list = []
    let mutable lastExecutionAt : DateTime option = None

    do fsi.Start()

    let buildState () : FsiRemoteSessionState =
        { SessionId = sessionId
          SessionName = sessionId
          Status = statusToString status
          Refs = List.rev refs
          Loads = List.rev loads
          SearchPaths = List.rev searchPaths
          Variables = []
          LastCheckpointId = None
          RunningSinceUtc = Some runningSinceUtc
          LastExecutionAt = lastExecutionAt }

    let updateState (req: FsiRemoteCommandRequest) (result: FsiResult) =
        lastExecutionAt <- Some DateTime.UtcNow
        status <- if result.IsSuccess then SessionReady else SessionFaulted

        if result.IsSuccess then
            match req.CommandType with
            | "REFERENCE_ASSEMBLY"
            | "REFERENCE_NUGET" -> refs <- req.Payload :: refs
            | "LOAD" -> loads <- req.Payload :: loads
            | "ADD_PATH" -> searchPaths <- req.Payload :: searchPaths
            | "RESET"
            | "RESTART" ->
                refs <- []
                loads <- []
                searchPaths <- []
            | _ -> ()

    let handleRequest (req: FsiRemoteCommandRequest) =
        try
            let result =
                match req.CommandType with
                | "EXEC" -> fsi.ExecuteInteraction(req.Payload)
                | "EVAL" -> fsi.EvaluateExpression(req.Payload)
                | "LOAD" -> fsi.ExecuteInteraction($"#load \"{req.Payload}\"")
                | "PARSE" -> fsi.ParseAndCheck(req.Payload)
                | "REFERENCE_NUGET" ->
                    fsi.ReferenceNugetPackage(req.Payload, ?usePackageTargets = req.UsePackageTargets)
                | "REFERENCE_ASSEMBLY" -> fsi.ReferenceAssembly(req.Payload)
                | "ADD_PATH" -> fsi.AddSearchPath(req.Payload)
                | "RESET" ->
                    fsi.Reset()
                    { FsiResult.empty with
                        Output = "FSI session reset"
                        Value = Some sessionId }
                | "RESTART" ->
                    fsi.Restart()
                    { FsiResult.empty with
                        Output = "FSI session restarted"
                        Value = Some sessionId }
                | "STATE" ->
                    { FsiResult.empty with
                        Output = fsi.GetState()
                        Value = Some sessionId }
                | unknown ->
                    { FsiResult.empty with
                        Errors = $"Unsupported remote FSI command: {unknown}"
                        IsSuccess = false }

            updateState req result
            toRemoteResult result
        with ex ->
            status <- SessionFaulted
            lastExecutionAt <- Some DateTime.UtcNow
            failureResult ex.Message (Some(ex.GetType().FullName))

    override this.Receive(message: obj) =
        match message with
        | :? FsiRemoteCommandRequest as req ->
            let hostId = req.Route |> Option.bind (fun route -> route.HostId)

            let response : FsiRemoteCommandResponse =
                { RequestId = req.RequestId
                  HostId = hostId
                  SessionId = Some sessionId
                  Result = handleRequest req
                  SessionState = Some(buildState ()) }

            this.Sender.Tell(response)
            true
        | _ -> false

    static member Props(sessionId: string, config: FsiConfig) =
        Props.Create<SessionActor>(sessionId, config)
