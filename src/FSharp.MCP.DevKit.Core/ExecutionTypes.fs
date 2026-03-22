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
