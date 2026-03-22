# SD

## 設計定位

本輪設計不是補幾個 MCP tool，而是把 `FSharp.MCP.DevKit` 重構成：

1. `control plane`
2. `execution plane`
3. `result plane`

三層分離的系統。

目標能力：

1. 多 agent
2. 多 host
3. 多 session
4. dual-backend
5. backward compatibility
6. async-first
7. diagnostics-first
8. result traceability 與 result set operation

## 核心決策摘要

### 決策 1. `FsiResult` 保持純 payload

`FsiResult` 不直接混入 routing metadata。  
原因：

1. 它應代表單次 FSI 執行的語意結果
2. `backendKind/hostId/sessionId/resultId` 是 trace metadata，不是 payload
3. 若直接混入，cache、比對、集合運算、序列化都會被污染

因此改為兩層：

1. `FsiResult`
2. `FsiExecutionRecord`

### 決策 2. execution metadata 進 `FsiExecutionRecord`

新增 envelope：

- `ResultId`
- `RequestId`
- `AgentId`
- `BackendKind`
- `HostId`
- `SessionId`
- `OperationKind`
- `SubmittedAt`
- `StartedAt`
- `CompletedAt`
- `RawErrorType option`
- `Result : FsiResult`

### 決策 3. quotation / result set operation 不直接污染 execution contract

允許 agent 對結果集做 query，但不把 F# quotation object 直接當 transport payload。

第一階段：

- 以 `ResultId seq` 做 query input
- query language 先支援：
  - built-in ops
  - `fsharpCode : string`

第二階段才考慮顯式 quotation text，例如：

- `<@ fun op_req_id bk1 hid1 sid1 rid1 bk2 hid2 sid2 rid2 -> ... @>`

但 transport 層仍然只收字串與結構化 request，不直接跨 runtime 傳 `Expr`

### 決策 4. 所有 out-of-proc host 建立流程強制經 `ProcSupervisor`

正式規則：

1. `create_fsi_host` 只允許建立 `netfx` 與 `net10`
2. 這兩種 host 都必須經 `ProcSupervisor`
3. `inproc` 不屬於正式 provisioning 目標，只保留給 backward compatibility 的 `default-host`

### 決策 5. `.NET 10` host / proc lifecycle 採外部套件

正式納入：

- `FAkka.FSI.Supervisor 1.562.100.201-dgx.1`
- `FAkka.Proc.Supervisor 1.562.100.201-dgx.2`

角色：

- `FAkka.FSI.Supervisor`
  - `.NET 10` host 內的 multi-session FSI execution runtime
- `FAkka.Proc.Supervisor`
  - process start / probe / restart / registry

### 決策 6. `netfx` host 沿用現有程式碼，但補 multi-session

`src/FSharp.MCP.DevKit.FsiHost` 不重做，只升級為：

- host actor
- session actor dictionary
- route-aware transport

### 決策 7. 所有 host session 一律 `actor per session`

正式規則：

1. `netfx` host 採 `HostSupervisorActor -> SessionActor`
2. `net10` host 也以 session actor 為基本隔離單位
3. session actor 只處理 execution / state / checkpoint
4. `result_op` 不在 session actor 內做
5. `result_op` 由 parent / supervisor 取 `ResultRegistry` 後交給 `ResultQueryService`

### 決策 8. 舊工具維持 implicit default routing

舊工具仍可用，對應：

- `default-agent`
- `default-host`
- `default-session`

新工具才顯式收：

- `agentId`
- `hostId`
- `sessionId`

## 系統分層

```text
MCP Client / Agent
    |
    v
Server Tools / Resources / HTTP
    |
    v
Control Plane
    |
    +-- AgentRegistry
    +-- HostRegistry
    +-- SessionRegistry
    +-- AsyncJobRegistry
    +-- ResultRegistry
    +-- PathMappingRegistry
    +-- ExecutionRouter
    |
    v
Execution Plane
    |
    +-- InProcBackend
    +-- NetFxHostBackend
    +-- Net10HostBackend
            |
            +-- FAkka.Proc.Supervisor
            +-- FAkka.FSI.Supervisor
    |
    v
Result Plane
    |
    +-- FsiExecutionRecord
    +-- ResultQueryService
    +-- ResultSet operations
```

## 專案與 module hierarchy

## `src/FSharp.MCP.DevKit.Core`

保留：

- `FSIService.fs`

新增：

- `ExecutionTypes.fs`
- `ResultTypes.fs`

責任：

- 純 domain types
- 不碰 Akka
- 不碰 ASP.NET
- 不碰 MCP SDK

建議 module：

```fsharp
namespace FSharp.MCP.DevKit.Core

module ExecutionTypes
module ResultTypes
```

## `src/FSharp.MCP.DevKit.Messages`

擴充：

- remote DTO
- route metadata DTO

責任：

- host/server 之間的 transport-safe messages

建議 module：

```fsharp
namespace FSharp.MCP.DevKit.Messages

module RemoteContracts
```

## `src/FSharp.MCP.DevKit.Server`

新增子目錄：

```text
Server/
  ControlPlane/
    AgentRegistry.fs
    HostRegistry.fs
    SessionRegistry.fs
    AsyncJobRegistry.fs
    ResultRegistry.fs
    PathMappingRegistry.fs
  Routing/
    ExecutionRouter.fs
    DefaultRouting.fs
  Backends/
    IBackend.fs
    InProcBackend.fs
    NetFxHostBackend.fs
    Net10HostBackend.fs
  Integration/
    RemoteFsiClient.fs
    FsiSupervisorClient.fs
    ProcSupervisorClient.fs
  ResultQuery/
    ResultQueryTypes.fs
    ResultQueryService.fs
  Tools/
    McpFsiTools.fs
    McpControlPlaneTools.fs
    McpResultTools.fs
  Resources/
    FsiResources.fs
```

## `src/FSharp.MCP.DevKit.FsiHost`

新增：

- `HostSupervisorActor.fs`
- `SessionActor.fs`
- `HostContracts.fs` 若需要

現有：

- `Program.fs`

責任：

- `netfx` host runtime
- 單 host 多 session

## domain types

### 1. execution result types

```fsharp
namespace FSharp.MCP.DevKit.Core

type FsiDiagnostic =
    { FileName: string
      StartLine: int
      EndLine: int
      StartColumn: int
      EndColumn: int
      Severity: string
      Message: string }

type FsiResult =
    { Output: string
      Errors: string
      IsSuccess: bool
      ExecutionTime: System.TimeSpan option
      Diagnostics: FsiDiagnostic array
      Value: string option }

type BackendKind =
    | InProc
    | NetFxRemote
    | Net10Remote

type OperationKind =
    | ExecuteCode
    | EvaluateExpression
    | LoadScript
    | ReferenceAssembly
    | ReferenceNuget
    | AddSearchPath
    | ResetSession
    | RestartHost
    | GetState
    | ResultQuery

type FsiExecutionRecord =
    { ResultId: string
      RequestId: string
      AgentId: string
      BackendKind: BackendKind
      HostId: string
      SessionId: string
      OperationKind: OperationKind
      SubmittedAt: System.DateTime
      StartedAt: System.DateTime option
      CompletedAt: System.DateTime option
      RawErrorType: string option
      Result: FsiResult }
```

### 2. routing types

```fsharp
namespace FSharp.MCP.DevKit.Core

type AgentRecord =
    { AgentId: string
      DisplayName: string option
      CreatedAt: System.DateTime
      LastSeenAt: System.DateTime
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
      CreatedAt: System.DateTime
      LastHealthCheckAt: System.DateTime option
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
      RunningSinceUtc: System.DateTime option
      LastExecutionAt: System.DateTime option }

type ExecutionRoute =
    { AgentId: string
      HostId: string
      SessionId: string }
```

### 3. async job types

```fsharp
namespace FSharp.MCP.DevKit.Core

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
      SubmittedAt: System.DateTime
      StartedAt: System.DateTime option
      CompletedAt: System.DateTime option
      Status: AsyncJobStatus
      ResultId: string option
      Result: FsiResult option }
```

### 4. result query types

```fsharp
namespace FSharp.MCP.DevKit.Server.ResultQuery

type ResultQueryLanguage =
    | BuiltIn
    | FSharpCode

type ResultQueryKind =
    | Filter
    | Map
    | Exists
    | ForAll
    | Zip
    | Diff
    | GroupBy
    | Custom

type ResultQueryRequest =
    { QueryId: string
      AgentId: string
      PrimaryResultIds: string list
      SecondaryResultIds: string list
      Language: ResultQueryLanguage
      Kind: ResultQueryKind
      QueryText: string }

type ResultQueryResponse =
    { QueryId: string
      IsSuccess: bool
      Output: string
      Errors: string
      ProducedResultIds: string list
      MaterializedJson: string option }
```

## transport DTO

### current issue

現有 `FsiRemoteCommandRequest` 只有：

- `RequestId`
- `CommandType`
- `Payload`
- `UsePackageTargets`

這不夠支援 multi-tenant routing。

### new DTO

```fsharp
namespace FSharp.MCP.DevKit.Messages

type FsiRemoteRouteDto =
    { AgentId: string option
      HostId: string option
      SessionId: string option }

type FsiRemoteCommandRequest =
    { RequestId: string
      CommandType: string
      Payload: string
      Route: FsiRemoteRouteDto option
      UsePackageTargets: bool option
      TimeoutMs: int option }

type FsiRemoteResult =
    { Output: string
      Errors: string
      IsSuccess: bool
      ExecutionTimeMs: float option
      Diagnostics: FsiRemoteDiagnostic array
      Value: string option
      RawErrorType: string option }

type FsiRemoteCommandResponse =
    { RequestId: string
      HostId: string option
      SessionId: string option
      Result: FsiRemoteResult }
```

## backend contracts

```fsharp
namespace FSharp.MCP.DevKit.Server.Backends

type ExecutionRequest =
    { RequestId: string
      Route: ExecutionRoute
      OperationKind: OperationKind
      Payload: string
      Timeout: System.TimeSpan option
      UsePackageTargets: bool option }

type BackendHealth =
    { BackendKind: BackendKind
      IsAvailable: bool
      Message: string option
      HostId: string option
      CheckedAt: System.DateTime }

type IFsiExecutionBackend =
    abstract member BackendKind : BackendKind
    abstract member Execute : ExecutionRequest -> System.Threading.Tasks.Task<FsiExecutionRecord>
    abstract member GetSessionState : ExecutionRoute -> System.Threading.Tasks.Task<SessionRecord>
    abstract member ResetSession : ExecutionRoute -> System.Threading.Tasks.Task<FsiExecutionRecord>
    abstract member RestartHost : HostRecord -> System.Threading.Tasks.Task<unit>
    abstract member HealthCheck : HostRecord -> System.Threading.Tasks.Task<BackendHealth>
```

## registries

### interfaces

```fsharp
namespace FSharp.MCP.DevKit.Server.ControlPlane

type IAgentRegistry =
    abstract member Register : AgentRecord -> AgentRecord
    abstract member TryGet : string -> AgentRecord option
    abstract member Touch : string -> unit
    abstract member List : unit -> AgentRecord list

type IHostRegistry =
    abstract member Create : HostRecord -> HostRecord
    abstract member Update : HostRecord -> unit
    abstract member TryGet : string -> HostRecord option
    abstract member ListByAgent : string -> HostRecord list

type ISessionRegistry =
    abstract member Create : SessionRecord -> SessionRecord
    abstract member Update : SessionRecord -> unit
    abstract member TryGet : string * string -> SessionRecord option
    abstract member ListByHost : string -> SessionRecord list

type IAsyncJobRegistry =
    abstract member Create : AsyncFsiJob -> AsyncFsiJob
    abstract member MarkRunning : string * System.DateTime -> unit
    abstract member Complete : string * string * FsiResult * System.DateTime -> unit
    abstract member Fail : string * FsiResult * System.DateTime -> unit
    abstract member TryGet : string -> AsyncFsiJob option

type IResultRegistry =
    abstract member Put : FsiExecutionRecord -> unit
    abstract member TryGet : string -> FsiExecutionRecord option
    abstract member ListBySession : ExecutionRoute -> FsiExecutionRecord list
    abstract member ListByAgent : string -> FsiExecutionRecord list
```

### initial implementation strategy

第一版先用 in-memory concurrent dictionary。  
第二版再考慮 durable storage。

## routing functions

### `DefaultRouting.resolve`

```fsharp
val resolve :
    agentRegistry: IAgentRegistry ->
    hostRegistry: IHostRegistry ->
    sessionRegistry: ISessionRegistry ->
    requestedRoute: ExecutionRoute option ->
    ExecutionRoute
```

偽碼：

```fsharp
match requestedRoute with
| Some route -> validate route and return route
| None ->
    let agentId = "default-agent"
    ensure default agent exists
    ensure default host exists
    ensure default session exists
    { AgentId = agentId; HostId = "default-host"; SessionId = "default-session" }
```

### `ExecutionRouter.routeAndExecute`

```fsharp
val routeAndExecute :
    request: ExecutionRequest ->
    Task<FsiExecutionRecord>
```

偽碼：

```fsharp
let host = hostRegistry.TryGet request.Route.HostId |> require
let backend = backendSelector.Resolve host
let result = backend.Execute request
resultRegistry.Put result
sessionRegistry.Update (deriveSessionUpdate result)
return result
```

## host lifecycle functions

### `HostProvisioningService.createHost`

```fsharp
val createHost :
    agentId: string ->
    hostKind: HostKind ->
    requestedHostId: string option ->
    Task<HostRecord>
```

偽碼：

```fsharp
register or validate agent
let hostId = requestedHostId |> defaultGuidBased
let hostRecord = create Creating record
hostRegistry.Create hostRecord

match hostKind with
| InProcHost ->
    fail "explicit host creation does not support inproc"
| NetFxHost ->
    procSupervisorClient.StartProc(netfxSpec)
    wait until health check ok
    update address/proc/state
| Net10Host ->
    procSupervisorClient.StartProc(...)
    wait for proc snapshot ready
    bind host to returned address/procId

return updatedHost
```

### `SessionProvisioningService.createSession`

```fsharp
val createSession :
    routeBase: { AgentId: string; HostId: string } ->
    sessionName: string option ->
    sessionId: string option ->
    Task<SessionRecord>
```

偽碼：

```fsharp
validate host exists and belongs to agent
let sessionId = provided or generated
backend.EnsureSession(route)
let initialState = backend.GetSessionState(route)
sessionRegistry.Create initialState
return initialState
```

## async queue functions

### `AsyncExecutionService.enqueue`

```fsharp
val enqueue :
    request: ExecutionRequest ->
    Task<{ AsyncId: string; RequestId: string; Route: ExecutionRoute; SubmittedAt: DateTime }>
```

偽碼：

```fsharp
let asyncId = guid()
let job = create queued job
jobRegistry.Create job
channel.Writer.TryWrite(jobRequest)
return ack
```

### `AsyncExecutionService.processLoop`

```fsharp
val processLoop : CancellationToken -> Task
```

偽碼：

```fsharp
while not cancelled do
    let jobRequest = read channel
    jobRegistry.MarkRunning(...)
    try
        let resultRecord = router.routeAndExecute(jobRequest.ExecutionRequest)
        jobRegistry.Complete(jobRequest.AsyncId, resultRecord.ResultId, resultRecord.Result, now)
    with ex ->
        let failed = map ex -> FsiResult
        jobRegistry.Fail(jobRequest.AsyncId, failed, now)
```

## result query design

### design goal

支援 agent 對歷史結果做：

1. 單筆 trace
2. 多筆集合運算
3. 比較不同 backend / host / session 的行為差異

### why record IDs first

agent 不應自己保整包結果再計算。  
更合理的是：

1. execution 產出 `ResultId`
2. agent 用 `ResultId seq` 提 query
3. server 在受控 query session 中計算

### exposed API

```fsharp
val runQuery :
    ResultQueryRequest ->
    Task<ResultQueryResponse>
```

### built-in ops

第一版內建：

1. `exists`
2. `forall`
3. `map`
4. `filter`
5. `zip`
6. `diff`
7. `groupBy`

### custom fsharp query

第二版支援 `QueryLanguage = FSharpCode`

輸入範例概念上可長成：

```fsharp
fun (records1: FsiExecutionRecord seq) (records2: FsiExecutionRecord seq) ->
    Seq.forall2 (fun r1 r2 -> r1.Result.IsSuccess = r2.Result.IsSuccess) records1 records2
```

注意：

- server 收的是 `string`
- 不直接跨 process 傳 `Expr`
- 在受控 query FSI session 內執行
- 第一版會先綁定 `records1/records2` 與 `primaryRecords/secondaryRecords`
- 若 `queryText` 是 `fun records1 records2 -> ...` 形式，server 會自動套用兩個結果集參數
- query 結果優先 materialize 成 JSON；若物件無法直接序列化，至少保留人類可讀的 `Output`

## MCP tools / resources

### backward-compatible tools

保留：

1. `execute_f_sharp_code`
2. `execute_f_sharp_code_async`
3. `evaluate_f_sharp_expression`
4. `load_f_sharp_script`
5. `reference_assembly`
6. `reference_nu_get_package`
7. `add_search_path`
8. `restart_fsi_session`

內部流程：

```fsharp
resolve default route
build ExecutionRequest
router.routeAndExecute or asyncService.enqueue
return old shape response
```

### new control-plane tools

```text
register_fsi_agent
create_fsi_host
list_fsi_hosts
create_fsi_session
list_fsi_sessions
delete_fsi_session
execute_f_sharp_code_routed
execute_f_sharp_code_routed_async
get_fsi_host_health
get_fsi_path_mappings
```

`create_fsi_host` 限制：

1. 只接受 `netfx` / `net10`
2. 一律透過 `ProcSupervisorClient`
3. 不接受 `inproc`

### new result-plane tools

```text
get_fsi_result
list_fsi_results
query_fsi_results
compare_fsi_results
```

### resource templates

```text
fsi/async/{asyncId}
fsi/agents/{agentId}
fsi/hosts/{hostId}
fsi/hosts/{hostId}/sessions
fsi/hosts/{hostId}/sessions/{sessionId}
fsi/results/{resultId}
fsi/path-mappings
```

## server-side function grouping

### `McpControlPlaneTools`

應包含：

```fsharp
static member RegisterFsiAgent(...)
static member CreateFsiHost(...)
static member ListFsiHosts(...)
static member CreateFsiSession(...)
static member ListFsiSessions(...)
```

### `McpFsiTools`

應只保留 execution-oriented tools：

```fsharp
static member ExecuteFSharpCode(...)
static member ExecuteFSharpCodeAsync(...)
static member EvaluateFSharpExpression(...)
static member LoadFSharpScript(...)
static member ReferenceAssembly(...)
static member ReferenceNuGetPackage(...)
static member AddSearchPath(...)
static member RestartFsiSession(...)
```

### `McpResultTools`

應包含：

```fsharp
static member GetFsiResult(...)
static member ListFsiResults(...)
static member QueryFsiResults(...)
static member CompareFsiResults(...)
```

## netfx host refactor

### current state

現在 `FsiActor` 直接持有單一 `FsiService`，等於：

- actor lifecycle = host lifecycle = session lifecycle

### target state

改成：

```text
HostSupervisorActor
    |
    +-- session-1 -> SessionActor(FsiService)
    +-- session-2 -> SessionActor(FsiService)
    +-- session-3 -> SessionActor(FsiService)
    |
    +-- result-op coordinator -> ResultQueryService
```

### host-side functions

```fsharp
member GetOrCreateSessionActor : sessionId:string -> IActorRef
member TryGetSessionActor : sessionId:string -> IActorRef option
member HandleRemoteCommand : FsiRemoteCommandRequest -> unit
member HandleResultOperation : ResultQueryRequest -> unit
```

偽碼：

```fsharp
match request.Route with
| None -> route to default session
| Some route -> route to route.SessionId
```

`HandleResultOperation` 偽碼：

```fsharp
validate request.AgentId / HostId ownership
let records1 = resultRegistry.GetMany request.PrimaryResultIds
let records2 = resultRegistry.GetMany request.SecondaryResultIds
let response = resultQueryService.Run(request, records1, records2)
reply response
```

## net10 host integration

### `ProcSupervisorClient`

責任：

1. start host process
2. stop host process
3. get host snapshot
4. trigger restart
5. 作為所有 out-of-proc host 的唯一 provisioning 入口

### `FsiSupervisorClient`

責任：

1. execute code
2. get session info
3. list sessions
4. checkpoint / fork / join

### `Net10HostBackend.Execute`

偽碼：

```fsharp
map ExecutionRequest -> FAkka ExecCode
let execResult = fsiSupervisorClient.Execute(...)
let record = adapter.ToExecutionRecord(...)
resultRegistry.Put record
return record
```

`Net10HostBackend` host/session 原則：

1. host 建立流程一律經 `ProcSupervisor`
2. host 內一律 `actor per session`
3. `result_op` 由 parent / supervisor 協調，不在單一 session actor 內執行

## path mapping design

### type

```fsharp
type PathMappingRecord =
    { MappingId: string
      AgentId: string option
      HostId: string option
      ContainerPath: string
      HostPath: string
      MappingKind: string
      CreatedAt: System.DateTime }
```

### usage

1. host 建立時可註冊 mount roots
2. execution 失敗時可用於把 host-side path 映回 caller 可理解的 path
3. MCP resource `fsi/path-mappings` 直接暴露

## concrete implementation guidance

### phase 1. 先落 type 與 interfaces

先做：

1. `Core/ExecutionTypes.fs`
2. `Core/ResultTypes.fs`
3. `Server/Backends/IBackend.fs`
4. `Server/ControlPlane/*Registry.fs`
5. `Server/ResultQuery/ResultQueryTypes.fs`

此階段不改行為，只建立編譯基礎。

### phase 2. 切 `McpFsiTools.fs`

目標：

1. 把 registry / routing / queue 從 `FsiMcpService` 拆出
2. `FsiMcpService` 最後只保留 facade 或被淘汰

### phase 3. 讓舊工具先走 default routing

先不改舊 tool signature，只把內部改成：

```fsharp
let route = defaultRouting.Resolve None
router.routeAndExecute { Route = route; ... }
```

### phase 4. 補 control-plane tools

這一步完成後，多 agent / 多 host / 多 session 才真正可用。

### phase 5. 補 result plane

先做：

1. `ResultId`
2. `ResultRegistry`
3. `get/list/compare/query`

quotation style query 可先以 `FSharpCode` 字串落地。

## 驗證策略

### 最低驗收

1. 舊工具仍可用
2. 同一 agent 可建立兩個 host
3. 同一 host 可建立兩個 session
4. async job 可查 `agentId/hostId/sessionId/resultId`
5. 單筆 result 可用 `resultId` 查回
6. 兩組 resultId 可做 compare / forall / exists

### production-readiness 驗收

1. `net10` host 由 `FAkka.Proc.Supervisor` 管理
2. `net10` host 內可列出多個 session
3. `netfx` host 也能多 session
4. `/healthz` 不只回 server 狀態，也能查 host/session health resource

## SD 結論

本版 SD 的落點是：

1. `FsiResult` 保持乾淨
2. trace metadata 進 `FsiExecutionRecord`
3. result set operation 走 `ResultRegistry + ResultQueryService`
4. dual-backend 透過 `IFsiExecutionBackend` 收斂
5. 所有 out-of-proc host 建立流程一律經 `ProcSupervisor`
6. 所有 host session 一律 `actor per session`
7. `result_op` 由 parent + `ResultQueryService` 處理
8. `net10` host / proc lifecycle 交給 `FAkka.*`
9. `netfx` host 補 multi-session
10. 舊工具保相容，新工具顯式多租戶

下一步 `WBS` 將直接按這份 type/module/function 分解。
