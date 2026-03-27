#r @"/app/Akka.dll"
#r @"/app/Akka.Remote.dll"
#r @"/app/Akka.Cluster.dll"
#r @"/app/Akka.Cluster.Sharding.dll"
#r @"/app/Akka.DistributedData.dll"
#r @"/app/FAkka.Fsi.Contracts.dll"
#r @"/app/Akka.Proc.Supervisor.dll"
open System
open Akka.Actor
open Akka.Configuration
open Akka.FSI.Contracts
open Akka.Proc.Supervisor
let assemblies = [ typeof<Akka.FSI.Contracts.IMessage>.Assembly; typeof<ProcSnapshot>.Assembly ]
let baseCfg = ConfigurationFactory.ParseString(@"
akka {
  actor.provider = remote
  remote.dot-netty.tcp {
    hostname = ""127.0.0.1""
    port = 0
  }
}")
let cfg = (Akka.FSI.Contracts.ContractSerialization.configForAssemblies assemblies).WithFallback(baseCfg)
let system = ActorSystem.Create("verify-live-client", cfg)
try
  let procPath = "akka.tcp://proc-system@10.28.112.140:8110/user/proc-supervisor"
  let proc = system.ActorSelection(procPath)
  let procVersion = proc.Ask<OpResult>(OpCmd.GetVesion, TimeSpan.FromSeconds 10.0).Result
  printfn "PROC_VERSION=%s" (procVersion.informationalVersion |> Option.orElse procVersion.assemblyVersion |> Option.defaultValue "<none>")
  let snapshots = proc.Ask<ProcSnapshot array>(GetAllProcInfo, TimeSpan.FromSeconds 10.0).Result
  printfn "PROC_COUNT=%d" snapshots.Length
  for snap in snapshots do
    printfn "PROC %s status=%A fsi=%A node=%A" snap.procId snap.status snap.fsiSupervisorPath snap.nodeAddress
  match snapshots |> Array.tryFind (fun s -> s.fsiSupervisorPath.IsSome) with
  | None -> printfn "NO_FSI_SUPERVISOR"
  | Some snap ->
      let fsi = system.ActorSelection(snap.fsiSupervisorPath.Value)
      let resolved = fsi.ResolveOne(TimeSpan.FromSeconds 10.0).Result
      printfn "FSI_RESOLVED=%s" (resolved.Path.ToStringWithAddress())
      let sessions = fsi.Ask<Sessions>({ all = false }, TimeSpan.FromSeconds 10.0).Result
      printfn "FSI_SESSION_COUNT=%d" sessions.items.Length
      let fsiVersion = fsi.Ask<OpResult>(OpCmd.GetVesion, TimeSpan.FromSeconds 10.0).Result
      printfn "FSI_VERSION=%s" (fsiVersion.informationalVersion |> Option.orElse fsiVersion.assemblyVersion |> Option.defaultValue "<none>")
finally
  system.Terminate() |> ignore
