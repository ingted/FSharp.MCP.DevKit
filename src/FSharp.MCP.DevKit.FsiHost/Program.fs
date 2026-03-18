open System
open System.IO
open Akka.Actor
open Akka.Configuration
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Core.Actors

[<EntryPoint>]
let main argv =
    let configContent = File.ReadAllText("akka.conf")
    let config = ConfigurationFactory.ParseString(configContent)
    let system = ActorSystem.Create("FsiExecutionSystem", config)
    
    let fsiConfig = FsiConfig.defaultConfig
    let fsiActor = system.ActorOf(FsiActor.Props(fsiConfig), "fsiActor")
    
    printfn "FSI Execution Host started on port 8081..."
    
    while true do
        System.Threading.Thread.Sleep(5000)
    
    0