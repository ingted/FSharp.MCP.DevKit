module FSharp.MCP.DevKit.FsiHost.HostSupervisorActor

open System
open System.Collections.Generic
open Akka.Actor
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Messages
open FSharp.MCP.DevKit.FsiHost.ActorHelpers
open FSharp.MCP.DevKit.FsiHost.SessionActor

type HostSupervisorActor(config: FsiConfig) =
    inherit ActorBase()

    let sessionActors = Dictionary<string, IActorRef>()

    let resolveSessionId (route: FsiRemoteRouteDto option) =
        route
        |> Option.bind (fun value -> value.SessionId)
        |> Option.defaultValue DefaultSessionId

    let hostResponse (requestId: string) (route: FsiRemoteRouteDto option) (result: FsiRemoteResult) (sessionState: FsiRemoteSessionState option) =
        { RequestId = requestId
          HostId = route |> Option.bind (fun value -> value.HostId)
          SessionId = route |> Option.bind (fun value -> value.SessionId)
          Result = result
          SessionState = sessionState }

    member _.ActorCtx: IActorContext = ActorBase.Context

    override this.Receive(message: obj) =
        match message with
        | :? FsiRemoteCommandRequest as req ->
            let getOrCreateSessionActor (sessionId: string) =
                match sessionActors.TryGetValue sessionId with
                | true, actorRef -> actorRef
                | false, _ ->
                    let actorRef =
                        this.ActorCtx.ActorOf(SessionActor.Props(sessionId, config), $"session-{Guid.NewGuid():N}")

                    sessionActors.[sessionId] <- actorRef
                    actorRef

            let stopAllSessions () =
                for actorRef in sessionActors.Values do
                    this.ActorCtx.Stop actorRef

                sessionActors.Clear()

            match req.CommandType with
            | "PING" ->
                this.Sender.Tell(hostResponse req.RequestId req.Route (successResult "PONG") None)
                true
            | "RESTART_HOST" ->
                stopAllSessions ()
                this.Sender.Tell(hostResponse req.RequestId req.Route (successResult "FSI host restarted") None)
                true
            | "LIST_SESSIONS" ->
                let output = sessionActors.Keys |> Seq.sort |> String.concat "\n"
                this.Sender.Tell(hostResponse req.RequestId req.Route (successResult output) None)
                true
            | "RESULT_OP" ->
                this.Sender.Tell(
                    hostResponse
                        req.RequestId
                        req.Route
                        (failureResult "RESULT_OP is not supported by netfx host parent yet." (Some "UnsupportedOperationException"))
                        None
                )
                true
            | _ ->
                let sessionId = resolveSessionId req.Route
                let actorRef = getOrCreateSessionActor sessionId
                actorRef.Forward(req)
                true
        | _ -> false

    static member Props(config: FsiConfig) =
        Props.Create<HostSupervisorActor>(config)
