namespace FSharp.MCP.DevKit.Server.ControlPlane

open System
open FSharp.MCP.DevKit.Core

type IAgentRegistry =
    abstract member Register: AgentRecord -> AgentRecord
    abstract member TryGet: string -> AgentRecord option
    abstract member Touch: string -> unit
    abstract member List: unit -> AgentRecord list

type IHostRegistry =
    abstract member Create: HostRecord -> HostRecord
    abstract member Update: HostRecord -> unit
    abstract member TryGet: string -> HostRecord option
    abstract member ListByAgent: string -> HostRecord list

type ISessionRegistry =
    abstract member Create: SessionRecord -> SessionRecord
    abstract member Update: SessionRecord -> unit
    abstract member TryGet: string * string -> SessionRecord option
    abstract member ListByHost: string -> SessionRecord list

type InventoryEventRecord =
    { SequenceId: int64
      EventKind: string
      SubjectKind: string
      AgentId: string option
      HostId: string option
      SessionId: string option
      CreatedAt: DateTime
      Message: string option }

type IInventoryEventStore =
    abstract member Append: InventoryEventRecord -> InventoryEventRecord
    abstract member List: ?afterSequenceId: int64 * ?limit: int -> InventoryEventRecord list

type OutputSubscriberRecord =
    { SessionId: string
      SubscriberId: string
      FromSequenceNo: int64
      IncludeHistory: bool
      SubscribedAt: DateTime }

type OutputEventRecord =
    { SessionId: string
      ExecutionId: string option
      SequenceNo: int64
      StreamKind: string
      TimestampUtc: DateTime
      Payload: string
      IsReplay: bool }

type SessionOutputArchiveRecord =
    { SessionId: string
      ArchivedAt: DateTime
      EventCount: int
      MaxSequenceNo: int64 option }

type SessionOutputSealPendingRecord =
    { SessionId: string
      PendingAt: DateTime
      EventCount: int
      MaxSequenceNo: int64 option
      ErrorMessage: string }

type SessionOutputSealOutcome =
    | Archived of SessionOutputArchiveRecord
    | SealPending of SessionOutputSealPendingRecord

type IOutputSubscriberBroker =
    abstract member Subscribe: OutputSubscriberRecord -> OutputSubscriberRecord
    abstract member Unsubscribe: sessionId: string * subscriberId: string -> bool
    abstract member ListSubscribers: sessionId: string -> OutputSubscriberRecord list
    abstract member Publish: OutputEventRecord -> OutputEventRecord * OutputSubscriberRecord list
    abstract member ListEvents: sessionId: string * ?afterSequenceNo: int64 * ?limit: int -> OutputEventRecord list
    abstract member ClearSessionEvents: sessionId: string -> int

type ISessionOutputLiveStore =
    abstract member Append: eventRecord: OutputEventRecord -> unit
    abstract member ListEvents: sessionId: string * ?afterSequenceNo: int64 * ?limit: int -> OutputEventRecord list
    abstract member ClearSession: sessionId: string -> unit

type ISessionOutputArchiveStore =
    abstract member Seal: sessionId: string * events: OutputEventRecord list * archivedAt: DateTime -> SessionOutputArchiveRecord
    abstract member ListEvents: sessionId: string * ?afterSequenceNo: int64 * ?limit: int -> OutputEventRecord list
    abstract member ListArchives: ?limit: int -> SessionOutputArchiveRecord list
    abstract member TryGetArchive: sessionId: string -> SessionOutputArchiveRecord option
    abstract member MarkSealPending:
        sessionId: string * events: OutputEventRecord list * pendingAt: DateTime * errorMessage: string -> SessionOutputSealPendingRecord
    abstract member ListPendingEvents: sessionId: string * ?afterSequenceNo: int64 * ?limit: int -> OutputEventRecord list
    abstract member TryGetSealPending: sessionId: string -> SessionOutputSealPendingRecord option
    abstract member RecoverSealPending: sessionId: string -> SessionOutputArchiveRecord option

type IAsyncJobRegistry =
    abstract member Create: AsyncFsiJob -> AsyncFsiJob
    abstract member MarkRunning: string * DateTime -> unit
    abstract member Complete: string * string * FsiResult * DateTime -> unit
    abstract member Fail: string * FsiResult * DateTime -> unit
    abstract member TryGet: string -> AsyncFsiJob option
    abstract member ListByRoute: ExecutionRoute -> AsyncFsiJob list

type IResultRegistry =
    abstract member Put: FsiExecutionRecord -> unit
    abstract member TryGet: string -> FsiExecutionRecord option
    abstract member ListBySession: ExecutionRoute -> FsiExecutionRecord list
    abstract member ListByAgent: string -> FsiExecutionRecord list

type PathMappingRecord =
    { MappingId: string
      AgentId: string option
      HostId: string option
      ContainerPath: string
      HostPath: string
      MappingKind: string
      CreatedAt: DateTime }

type IPathMappingRegistry =
    abstract member Put: PathMappingRecord -> unit
    abstract member List: unit -> PathMappingRecord list
    abstract member ListByAgent: string -> PathMappingRecord list
    abstract member ListByHost: string -> PathMappingRecord list
