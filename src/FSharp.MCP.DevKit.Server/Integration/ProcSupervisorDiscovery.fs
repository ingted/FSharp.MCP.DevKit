namespace FSharp.MCP.DevKit.Server.Integration

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

[<CLIMutable>]
type ProcSupervisorResolution =
    { ActorPath: string
      BaseUrl: string option
      Source: string }

module ProcSupervisorDiscovery =
    [<Literal>]
    let DefaultLocalActorPath = "akka.tcp://proc-system@127.0.0.1:8110/user/proc-supervisor"

    let DefaultLocalBaseUrls =
        [ "http://127.0.0.1:6001"
          "http://localhost:6001" ]

    let isActorPath (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && (value.StartsWith("akka://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("akka.tcp://", StringComparison.OrdinalIgnoreCase))

    let tryNormalizeBaseUrl (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            let trimmed = value.Trim()
            let candidate =
                if trimmed.Contains("://", StringComparison.Ordinal) then
                    trimmed
                else
                    "http://" + trimmed

            match Uri.TryCreate(candidate, UriKind.Absolute) with
            | true, uri when uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                              || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ->
                let builder = UriBuilder(uri)
                Some(builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'))
            | _ -> None

    let tryActorPathFromClusterAddress (clusterAddress: string) =
        if String.IsNullOrWhiteSpace clusterAddress then
            None
        else
            Some(clusterAddress.TrimEnd('/') + "/user/proc-supervisor")

    let tryParseClusterAddressFromClusterInfoJson (json: string) =
        if String.IsNullOrWhiteSpace json then
            None
        else
            try
                use doc = JsonDocument.Parse(json)
                match doc.RootElement.TryGetProperty("address") with
                | true, value when value.ValueKind = JsonValueKind.String ->
                    value.GetString() |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
                | _ -> None
            with _ ->
                None

    let tryActorPathFromClusterInfoJson (json: string) =
        tryParseClusterAddressFromClusterInfoJson json
        |> Option.bind tryActorPathFromClusterAddress

    let private tryDiscoverFromBaseUrl (httpClient: HttpClient) (baseUrl: string) =
        task {
            try
                let requestUri = Uri(baseUrl.TrimEnd('/') + "/api/cluster/info")
                use request = new HttpRequestMessage(HttpMethod.Get, requestUri)
                use! response = httpClient.SendAsync(request)
                if not response.IsSuccessStatusCode then
                    return None
                else
                    let! json = response.Content.ReadAsStringAsync()
                    return
                        tryActorPathFromClusterInfoJson json
                        |> Option.map (fun actorPath ->
                            { ActorPath = actorPath
                              BaseUrl = Some baseUrl
                              Source = "cluster-info-discovery" })
            with _ ->
                return None
        }

    let resolveActorPath
        (httpClient: HttpClient)
        (configuredPath: string option)
        (configuredBaseUrl: string option)
        =
        task {
            let configuredPathValue =
                configuredPath
                |> Option.bind (fun value -> if String.IsNullOrWhiteSpace value then None else Some(value.Trim()))

            match configuredPathValue with
            | Some actorPath when isActorPath actorPath ->
                return
                    { ActorPath = actorPath
                      BaseUrl = None
                      Source = "configured-actor-path" }
            | Some pathOrBaseUrl ->
                match tryNormalizeBaseUrl pathOrBaseUrl with
                | Some baseUrl ->
                    let! discovered = tryDiscoverFromBaseUrl httpClient baseUrl
                    match discovered with
                    | Some resolution ->
                        return { resolution with Source = "configured-base-url" }
                    | None ->
                        return
                            { ActorPath = DefaultLocalActorPath
                              BaseUrl = Some baseUrl
                              Source = "fallback-default-actor-path" }
                | None ->
                    return
                        { ActorPath = DefaultLocalActorPath
                          BaseUrl = None
                          Source = "fallback-default-actor-path" }
            | None ->
                let baseUrlCandidates =
                    [ configuredBaseUrl |> Option.bind tryNormalizeBaseUrl
                      yield! DefaultLocalBaseUrls |> List.map Some ]
                    |> List.choose id
                    |> List.distinct

                let mutable resolutionOpt = None
                let mutable index = 0

                while resolutionOpt.IsNone && index < baseUrlCandidates.Length do
                    let candidate = baseUrlCandidates[index]
                    let! discovered = tryDiscoverFromBaseUrl httpClient candidate
                    resolutionOpt <- discovered
                    index <- index + 1

                match resolutionOpt with
                | Some resolution -> return resolution
                | None ->
                    return
                        { ActorPath = DefaultLocalActorPath
                          BaseUrl = configuredBaseUrl |> Option.bind tryNormalizeBaseUrl
                          Source = "fallback-default-actor-path" }
        }
