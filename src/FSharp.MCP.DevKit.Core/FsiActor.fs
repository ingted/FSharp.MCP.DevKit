namespace FSharp.MCP.DevKit.Core.Actors

open Akka.Actor
open FSharp.MCP.DevKit.Core

type FsiActor(config: FsiConfig) =
    inherit ActorBase()
    
    let fsi = new FsiService(config)
    do fsi.Start()

    override this.Receive(message: obj) =
        match message with
        | :? FsiEvalRequest as req ->
            let result = 
                if req.IsExpression then
                    fsi.EvaluateExpression(req.Code)
                else
                    fsi.ExecuteInteraction(req.Code)
            
            this.Sender.Tell({ RequestId = req.RequestId; Result = result })
            true
        | _ -> false

    static member Props(config: FsiConfig) =
        Props.Create<FsiActor>(config)
