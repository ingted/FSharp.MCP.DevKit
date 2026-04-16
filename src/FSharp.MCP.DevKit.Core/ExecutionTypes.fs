namespace FSharp.MCP.DevKit.Core

open System

type AgentRecord =
    { AgentId: string
      DisplayName: string option
      CreatedAt: DateTime
      LastSeenAt: DateTime
      DefaultHostId: string option
      Metadata: Map<string, string> }

type HostKind =
    | InProcHost
    | NetFxHost
    | Net10Host

type HostStatus =
    | Creating
    | Ready
    | Busy
    | Degraded
    | Stopped
    | Faulted

type HostRecord =
    { HostId: string
      AgentId: string
      HostKind: HostKind
      BackendKind: BackendKind
      Status: HostStatus
      Address: string option
      ProcId: int option
      CreatedAt: DateTime
      LastHealthCheckAt: DateTime option
      LastError: string option }

type SessionStatus =
    | SessionReady
    | SessionBusy
    | SessionFaulted
    | SessionMissing

type SessionRecord =
    { SessionId: string
      AgentId: string
      HostId: string
      SessionName: string
      Status: SessionStatus
      Refs: string list
      Loads: string list
      SearchPaths: string list
      Variables: (string * string) list
      LastCheckpointId: string option
      RunningSinceUtc: DateTime option
      LastExecutionAt: DateTime option }

type ExecutionRoute =
    { AgentId: string
      HostId: string
      SessionId: string }

module PrincipalAttribution =
    [<Literal>]
    let PrincipalId = "principal.id"

    [<Literal>]
    let PrincipalKind = "principal.kind"

    [<Literal>]
    let PrincipalSource = "principal.source"

    [<Literal>]
    let PrincipalAgentId = "principal.agentId"

    [<Literal>]
    let PrincipalHostId = "principal.hostId"

    [<Literal>]
    let PrincipalSessionId = "principal.sessionId"

    [<Literal>]
    let ExecutionAgentId = "execution.agentId"

    [<Literal>]
    let ExecutionHostId = "execution.hostId"

    [<Literal>]
    let ExecutionSessionId = "execution.sessionId"

    let nonBlank value =
        value |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let firstNonBlank values =
        values |> List.tryPick nonBlank

    let addIfMissing key value metadata =
        if metadata |> Map.containsKey key then metadata else metadata |> Map.add key value

    let normalize (route: ExecutionRoute) (metadata: Map<string, string>) =
        let principalId =
            firstNonBlank
                [ metadata |> Map.tryFind PrincipalId
                  metadata |> Map.tryFind "requestedBy"
                  metadata |> Map.tryFind "caller.agentId"
                  Some route.AgentId ]
            |> Option.defaultValue route.AgentId

        let principalKind =
            firstNonBlank
                [ metadata |> Map.tryFind PrincipalKind
                  metadata |> Map.tryFind "requestedBy.kind"
                  metadata |> Map.tryFind "caller.kind" ]
            |> Option.defaultValue "agent"

        let principalSource =
            firstNonBlank [ metadata |> Map.tryFind PrincipalSource; Some "route" ]
            |> Option.defaultValue "route"

        metadata
        |> addIfMissing PrincipalId principalId
        |> addIfMissing PrincipalKind principalKind
        |> addIfMissing PrincipalSource principalSource
        |> addIfMissing PrincipalAgentId route.AgentId
        |> addIfMissing PrincipalHostId route.HostId
        |> addIfMissing PrincipalSessionId route.SessionId
        |> addIfMissing ExecutionAgentId route.AgentId
        |> addIfMissing ExecutionHostId route.HostId
        |> addIfMissing ExecutionSessionId route.SessionId

type AsyncJobStatus =
    | Queued
    | Running
    | Completed
    | Failed

type AsyncFsiJob =
    { AsyncId: string
      RequestId: string
      Route: ExecutionRoute
      OperationKind: OperationKind
      Payload: string
      SubmittedAt: DateTime
      StartedAt: DateTime option
      CompletedAt: DateTime option
      Status: AsyncJobStatus
      ResultId: string option
      Result: FsiResult option }
