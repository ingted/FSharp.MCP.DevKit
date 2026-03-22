namespace FSharp.MCP.DevKit.Server.Integration

open System
open System.Threading.Tasks
open Akka.Actor
open Akka.Proc.Supervisor

type ProcHostSpec =
    { ExecutablePath: string
      Arguments: string list
      WorkingDirectory: string option
      Role: string option
      ProbeMessage: string option
      ProbeCron: string option
      ProbeIntervalMs: int option }

type ProcHostSnapshot =
    { ProcId: string
      Status: string
      ProcessId: int option
      FsiSupervisorPath: string option
      NodeAddress: string option
      LastProbeUtc: DateTime option
      LastProbeOk: bool option
      ProbeFailures: int
      Spec: ProcHostSpec option
      LastError: string option }

type IProcSupervisorClient =
    abstract member StartProc: procId: string * spec: ProcHostSpec -> Task<ProcHostSnapshot>
    abstract member StopProc: procId: string * force: bool -> Task<ProcHostSnapshot>
    abstract member GetProcInfo: procId: string -> Task<ProcHostSnapshot option>
    abstract member ListProcInfo: unit -> Task<ProcHostSnapshot list>
    abstract member RestartProc: procId: string -> Task<ProcHostSnapshot>

module private ProcSupervisorAdapters =
    let toContractSpec (procId: string) (spec: ProcHostSpec) : ProcStartSpec =
        { procId = procId
          fileName = spec.ExecutablePath
          args = spec.Arguments
          workingDir = spec.WorkingDirectory
          role = spec.Role
          probeMessage = spec.ProbeMessage
          probeCron = spec.ProbeCron
          probeIntervalMs = spec.ProbeIntervalMs }

    let ofContractSpec (spec: ProcStartSpec) : ProcHostSpec =
        { ExecutablePath = spec.fileName
          Arguments = spec.args
          WorkingDirectory = spec.workingDir
          Role = spec.role
          ProbeMessage = spec.probeMessage
          ProbeCron = spec.probeCron
          ProbeIntervalMs = spec.probeIntervalMs }

    let ofSnapshot (snapshot: ProcSnapshot) : ProcHostSnapshot =
        { ProcId = snapshot.procId
          Status = snapshot.status
          ProcessId = snapshot.pid
          FsiSupervisorPath = snapshot.fsiSupervisorPath
          NodeAddress = snapshot.nodeAddress
          LastProbeUtc = snapshot.lastProbeUtc
          LastProbeOk = snapshot.lastProbeOk
          ProbeFailures = snapshot.probeFailures
          Spec = snapshot.spec |> Option.map ofContractSpec
          LastError = snapshot.lastError }

type ProcSupervisorClient(supervisor: IActorRef, ?defaultTimeout: TimeSpan) =
    let timeout = defaultArg defaultTimeout (TimeSpan.FromSeconds 5.0)

    let askSnapshot message = supervisor.Ask<ProcSnapshot>(message, timeout)

    interface IProcSupervisorClient with
        member _.StartProc(procId: string, spec: ProcHostSpec) =
            task {
                let startMessage =
                    { procId = procId
                      spec = ProcSupervisorAdapters.toContractSpec procId spec }

                let! snapshot = askSnapshot startMessage
                return ProcSupervisorAdapters.ofSnapshot snapshot
            }

        member _.StopProc(procId: string, force: bool) =
            task {
                let! snapshot = askSnapshot { procId = procId; force = force }
                return ProcSupervisorAdapters.ofSnapshot snapshot
            }

        member _.GetProcInfo(procId: string) =
            task {
                let! snapshot = askSnapshot { procId = procId }

                if String.IsNullOrWhiteSpace snapshot.procId then
                    return None
                else
                    return Some(ProcSupervisorAdapters.ofSnapshot snapshot)
            }

        member _.ListProcInfo() =
            task {
                let! snapshots = supervisor.Ask<ProcSnapshot[]>(GetAllProcInfo, timeout)
                return snapshots |> Array.toList |> List.map ProcSupervisorAdapters.ofSnapshot
            }

        member this.RestartProc(procId: string) =
            task {
                let client = this :> IProcSupervisorClient
                let! snapshot = client.GetProcInfo(procId)

                let current =
                    snapshot
                    |> Option.defaultWith (fun () -> invalidOp $"Process '{procId}' was not found in ProcSupervisor.")

                let spec =
                    current.Spec
                    |> Option.defaultWith (fun () -> invalidOp $"Process '{procId}' does not have a restartable spec.")

                let! _ = client.StopProc(procId, true)
                return! client.StartProc(procId, spec)
            }
