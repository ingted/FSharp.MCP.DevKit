module ProcSupervisorDiscoveryTests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.MCP.DevKit.Server.Integration

type private StubHttpMessageHandler(handler: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, _cancellationToken: CancellationToken) =
        handler request |> Task.FromResult

let private createHttpClient responder =
    let handler = new StubHttpMessageHandler(responder)
    new HttpClient(handler, true)

[<Fact>]
let ``ProcSupervisorDiscovery keeps configured actor path unchanged`` () =
    task {
        use httpClient =
            createHttpClient (fun _ -> new HttpResponseMessage(HttpStatusCode.NotFound))

        let! resolution =
            ProcSupervisorDiscovery.resolveActorPath
                httpClient
                (Some "akka.tcp://proc-system@127.0.0.1:8110/user/proc-supervisor")
                None

        Assert.Equal("akka.tcp://proc-system@127.0.0.1:8110/user/proc-supervisor", resolution.ActorPath)
        Assert.Equal("configured-actor-path", resolution.Source)
    }

[<Fact>]
let ``ProcSupervisorDiscovery derives actor path from cluster info json`` () =
    let json = """{"systemName":"proc-system","address":"akka.tcp://proc-system@10.0.0.5:11111","roles":["procnode"]}"""
    let actorPath = ProcSupervisorDiscovery.tryActorPathFromClusterInfoJson json
    Assert.Equal(Some "akka.tcp://proc-system@10.0.0.5:11111/user/proc-supervisor", actorPath)

[<Fact>]
let ``ProcSupervisorDiscovery uses configured base url when cluster info is reachable`` () =
    task {
        use httpClient =
            createHttpClient (fun request ->
                if request.RequestUri.AbsoluteUri = "http://127.0.0.1:6001/api/cluster/info" then
                    let response = new HttpResponseMessage(HttpStatusCode.OK)
                    response.Content <-
                        new StringContent("""{"systemName":"proc-system","address":"akka.tcp://proc-system@127.0.0.1:11111","roles":["procnode"]}""")
                    response
                else
                    new HttpResponseMessage(HttpStatusCode.NotFound))

        let! resolution =
            ProcSupervisorDiscovery.resolveActorPath
                httpClient
                (Some "http://127.0.0.1:6001")
                None

        Assert.Equal("akka.tcp://proc-system@127.0.0.1:11111/user/proc-supervisor", resolution.ActorPath)
        Assert.Equal(Some "http://127.0.0.1:6001", resolution.BaseUrl)
        Assert.Equal("configured-base-url", resolution.Source)
    }

[<Fact>]
let ``ProcSupervisorDiscovery probes default local base urls when explicit path is absent`` () =
    task {
        use httpClient =
            createHttpClient (fun request ->
                if request.RequestUri.AbsoluteUri = "http://127.0.0.1:6001/api/cluster/info" then
                    let response = new HttpResponseMessage(HttpStatusCode.OK)
                    response.Content <-
                        new StringContent("""{"systemName":"proc-system","address":"akka.tcp://proc-system@127.0.0.1:12000","roles":["procnode"]}""")
                    response
                else
                    new HttpResponseMessage(HttpStatusCode.NotFound))

        let! resolution =
            ProcSupervisorDiscovery.resolveActorPath httpClient None None

        Assert.Equal("akka.tcp://proc-system@127.0.0.1:12000/user/proc-supervisor", resolution.ActorPath)
        Assert.Equal(Some "http://127.0.0.1:6001", resolution.BaseUrl)
        Assert.Equal("cluster-info-discovery", resolution.Source)
    }

[<Fact>]
let ``ProcSupervisorDiscovery falls back to default actor path when no discovery endpoint is reachable`` () =
    task {
        use httpClient =
            createHttpClient (fun _ -> new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))

        let! resolution =
            ProcSupervisorDiscovery.resolveActorPath httpClient None (Some "http://127.0.0.1:6553")

        Assert.Equal(ProcSupervisorDiscovery.DefaultLocalActorPath, resolution.ActorPath)
        Assert.Equal(Some "http://127.0.0.1:6553", resolution.BaseUrl)
        Assert.Equal("fallback-default-actor-path", resolution.Source)
    }
