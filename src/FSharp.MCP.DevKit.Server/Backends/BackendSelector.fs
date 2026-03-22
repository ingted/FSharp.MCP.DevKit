namespace FSharp.MCP.DevKit.Server.Backends

open System
open FSharp.MCP.DevKit.Core

type BackendSelector(backends: seq<IFsiExecutionBackend>) =
    let backendByKind =
        backends
        |> Seq.distinctBy (fun backend -> backend.BackendKind)
        |> Seq.map (fun backend -> backend.BackendKind, backend)
        |> Map.ofSeq

    member _.TryResolve(backendKind: BackendKind) = backendByKind |> Map.tryFind backendKind

    member this.Resolve(backendKind: BackendKind) =
        match this.TryResolve backendKind with
        | Some backend -> backend
        | None -> invalidOp $"No execution backend registered for backend kind '{backendKind}'."

    member this.Resolve(hostKind: HostKind) =
        let backendKind =
            match hostKind with
            | InProcHost -> InProc
            | NetFxHost -> NetFxRemote
            | Net10Host -> Net10Remote

        this.Resolve backendKind

    member _.RegisteredBackendKinds = backendByKind |> Map.keys |> Seq.toArray
