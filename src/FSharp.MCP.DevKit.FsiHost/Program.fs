open System
open System.IO
open System.ServiceProcess
open System.Threading
open Akka.Actor
open Akka.Configuration
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.FsiHost.Actors

type FsiHostRuntime =
    { ActorSystem: ActorSystem
      ActorName: string }

let tryGetCommandLineValue (name: string) (argv: string array) =
    argv
    |> Array.tryFindIndex (fun arg -> arg.Equals(name, StringComparison.OrdinalIgnoreCase))
    |> Option.bind (fun index ->
        let valueIndex = index + 1
        if valueIndex < argv.Length then
            Some argv.[valueIndex]
        else
            None)

let getServiceName (argv: string array) =
    let envValue = Environment.GetEnvironmentVariable("FSIHOST_SERVICE_NAME")
    let argValue = tryGetCommandLineValue "--service-name" argv

    [ argValue; if not (String.IsNullOrWhiteSpace(envValue)) then Some envValue ]
    |> List.choose id
    |> List.tryFind (fun value -> not (String.IsNullOrWhiteSpace(value)))
    |> Option.defaultValue "fsihost"

let createRuntime () =
    let configPath = Path.Combine(AppContext.BaseDirectory, "akka.conf")
    let configContent = File.ReadAllText(configPath)
    let config = ConfigurationFactory.ParseString(configContent)
    let system = ActorSystem.Create("FsiExecutionSystem", config)
    let fsiConfig = FsiConfig.defaultConfig
    let actorName = "fsiActor"
    system.ActorOf(FsiActor.Props(fsiConfig), actorName) |> ignore

    printfn "FSI Execution Host started on port 8081..."

    { ActorSystem = system
      ActorName = actorName }

let stopRuntime (runtime: FsiHostRuntime) =
    runtime.ActorSystem.Terminate().GetAwaiter().GetResult()

type FsiHostWindowsService(serviceName: string) =
    inherit ServiceBase()

    let mutable runtime : FsiHostRuntime option = None

    do
        base.ServiceName <- serviceName
        base.AutoLog <- true
        base.CanStop <- true
        base.CanPauseAndContinue <- false

    override _.OnStart(_args: string array) =
        runtime <- Some(createRuntime ())

    override _.OnStop() =
        match runtime with
        | Some value ->
            stopRuntime value
            runtime <- None
        | None -> ()

let runConsole (serviceName: string) =
    use shutdownEvent = new ManualResetEvent(false)
    let runtime = createRuntime ()

    Console.CancelKeyPress.Add(fun args ->
        args.Cancel <- true
        shutdownEvent.Set() |> ignore)

    printfn $"Press Ctrl+C to stop {serviceName}."
    shutdownEvent.WaitOne() |> ignore
    stopRuntime runtime
    0

[<EntryPoint>]
let main argv =
    let serviceName = getServiceName argv

    let runAsService =
        (not Environment.UserInteractive)
        || (argv |> Array.exists (fun arg -> arg.Equals("--service", StringComparison.OrdinalIgnoreCase)))

    if runAsService then
        ServiceBase.Run([| new FsiHostWindowsService(serviceName) :> ServiceBase |])
        0
    else
        runConsole serviceName
