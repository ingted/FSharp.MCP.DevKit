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
