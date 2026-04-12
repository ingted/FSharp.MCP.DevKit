module BackendSelectorTests

open System
open System.Threading.Tasks
open Xunit
open FSharp.MCP.DevKit.Core
open FSharp.MCP.DevKit.Server.Backends

type private FakeBackend(kind: BackendKind) =
    interface IFsiExecutionBackend with
        member _.BackendKind = kind
        member _.Execute _ = Task.FromException<FsiExecutionRecord>(NotImplementedException())
        member _.EnsureSession _ = Task.FromException<SessionRecord>(NotImplementedException())
        member _.GetSessionState _ = Task.FromException<SessionRecord>(NotImplementedException())
        member _.ResetSession _ = Task.FromException<FsiExecutionRecord>(NotImplementedException())
        member _.RestartHost _ = Task.FromException<unit>(NotImplementedException())
        member _.HealthCheck _ = Task.FromException<BackendHealth>(NotImplementedException())

[<Fact>]
let ``Resolve maps InProcHost to InProc backend`` () =
    let backend = FakeBackend(InProc) :> IFsiExecutionBackend
    let selector = BackendSelector([ backend ])
    let resolved = selector.Resolve(InProcHost)
    Assert.Same(backend, resolved)

[<Fact>]
let ``Resolve maps NetFxHost to NetFxRemote backend`` () =
    let backend = FakeBackend(NetFxRemote) :> IFsiExecutionBackend
    let selector = BackendSelector([ backend ])
    let resolved = selector.Resolve(NetFxHost)
    Assert.Same(backend, resolved)

[<Fact>]
let ``Resolve maps Net10Host to Net10Remote backend`` () =
    let backend = FakeBackend(Net10Remote) :> IFsiExecutionBackend
    let selector = BackendSelector([ backend ])
    let resolved = selector.Resolve(Net10Host)
    Assert.Same(backend, resolved)

[<Fact>]
let ``Resolve throws when no backend is registered for host kind`` () =
    let selector = BackendSelector([])
    let ex = Assert.Throws<InvalidOperationException>(fun () -> selector.Resolve(Net10Host) |> ignore)
    Assert.Contains("Net10Remote", ex.Message)
