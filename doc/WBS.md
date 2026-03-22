# WBS

## 工作原則

本輪 WBS 依照新的 SD 拆成：

1. 先建型別與抽象
2. 再建 registry 與 router
3. 再接 backend
4. 再接 tools/resources
5. 再補 result plane 與 parent-level result operation
6. 最後做驗證

避免一開始就直接改 `McpFsiTools.fs` 成大泥球。

## 里程碑

| ID | 里程碑 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| M1 | 建立 shared domain / contracts | `Core`, `Messages`, backend interfaces | 編譯通過，無行為變更 | done |
| M2 | 建立 control plane | registries, router, default routing | 可註冊 agent/host/session | done |
| M3 | 完成 dual-backend 接線 | inproc, netfx, net10 backends | 可依 host kind 路由執行，且 out-of-proc 一律經 ProcSupervisor | done |
| M4 | 完成 MCP surface | tools/resources/http | 舊工具相容，新工具可顯式 routing | done |
| M5 | 完成 result plane | result registry, query service | built-in result plane 與 `FSharpCode` query 已可依 `ResultId` 查詢與集合運算 | done |
| M6 | 完成驗證與文件收尾 | logs, check, DevLog, QA evidence | `check.fsx` 無 FAIL | done |

## phase schedule

| Phase | 內容 | 主要檔案 | 依賴 | 進度 |
|---|---|---|---|---|
| P1 | shared type / module skeleton | `Core/*`, `Messages/*`, `Backends/IBackend.fs` | 無 | done |
| P2 | control plane registries + router | `Server/ControlPlane/*`, `Server/Routing/*` | P1 | done |
| P3 | inproc backend 遷移 | `Backends/InProcBackend.fs` | P1-P2 | done |
| P4 | netfx host multi-session | `FsiHost/*`, `Backends/NetFxHostBackend.fs` | P1-P2 | done |
| P5 | net10 host integration | `Integration/*`, `Backends/Net10HostBackend.fs` | P1-P2 | done |
| P6 | async queue 重切 | `AsyncJobRegistry`, tool facade | P2-P5 | done |
| P7 | MCP tools/resources | `Tools/*`, `Resources/*`, `Program.fs` | P2-P6 | done |
| P8 | result plane | `ResultRegistry`, `ResultQueryService`, result tools/resources | P1-P7 | done |
| P9 | tests / QA / doc closeout | `tests/*`, `doc/*`, logs | P1-P8 | done |

## work packages

## WP01 Shared Domain Types

目標：

建立新的 shared types，讓後續重構有穩定基底。

子任務：

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| T01 | 新增 `Core/ExecutionTypes.fs` | `AgentRecord`, `HostRecord`, `SessionRecord`, `ExecutionRoute`, `AsyncFsiJob` | 可編譯 | done |
| T02 | 新增 `Core/ResultTypes.fs` | `FsiResult`, `FsiExecutionRecord` | 可編譯 | done |
| T03 | 對齊現有 `FSIService.fs` 所用 `FsiResult` 映射 | adapter helpers | 現有結果可映到新契約 | done |
| T04 | 擴充 `Messages/McpActorMessages.fs` | route-aware DTO | host/server 可共用 | done |

依賴：

- 無

## WP02 Backend Abstraction

目標：

建立 `IFsiExecutionBackend`，避免 backend 差異直接滲進 tools。

子任務：

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| T05 | 新增 `Backends/IBackend.fs` | `ExecutionRequest`, `BackendHealth`, `IFsiExecutionBackend` | 可編譯 | done |
| T06 | 新增 backend selector | `BackendSelector` | 可依 `HostKind` resolve | done |
| T07 | 補 shared adapter helpers | result mapping / error mapping | `RawErrorType` 可填 | done |

依賴：

- WP01

## WP03 Control Plane Registries

目標：

建立四個 registry 與 default routing。

子任務：

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| T08 | 新增 `AgentRegistry.fs` | in-memory agent registry | 可 register/list/get | done |
| T09 | 新增 `HostRegistry.fs` | host registry | 可 create/update/list | done |
| T10 | 新增 `SessionRegistry.fs` | session registry | 可 create/update/list | done |
| T11 | 新增 `AsyncJobRegistry.fs` | async job registry | 可 create/running/complete/fail | done |
| T12 | 新增 `ResultRegistry.fs` | result registry | 可 put/get/list | done |
| T13 | 新增 `PathMappingRegistry.fs` | path mapping registry | 可 list mappings | done |
| T14 | 新增 `DefaultRouting.fs` | implicit default route resolver | 舊工具 route 可自動補齊 | done |
| T15 | 新增 `ExecutionRouter.fs` | 統一 route + execute orchestration | 所有核心 execution 已經由 router + backend path 統一處理 | done |

依賴：

- WP01
- WP02

## WP04 InProc Backend

目標：

讓現有 `FsiService` 可掛到新抽象下，作為 fallback/backward-compatible backend。

子任務：

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| T16 | 實作 `InProcBackend.fs` | in-proc backend | 可 execute/get state/reset | done |
| T17 | 加上 session dictionary | per-session in-proc handles | 不同 session state 可隔離 | done |
| T18 | 對齊 result record | `ResultId` 與 metadata 正常寫入 | result registry 與 session registry 已由 routed execution 寫入 | done |

依賴：

- WP02
- WP03

## WP05 NetFx Host Multi-Session

目標：

把現有 `FsiHost` 從單 session 升成 multi-session，且採 `actor per session`。

子任務：

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| T19 | 新增 `HostSupervisorActor.fs` | host-level router actor | 可接 route-aware request，並在 parent 層處理 host-level command | done |
| T20 | 新增 `SessionActor.fs` | per-session actor | 每個 session 各持一個 `FsiService` | done |
| T21 | 修改 `Program.fs` | host 啟動 supervisor actor | 不再只固定單 session 執行體 | done |
| T22 | 修改 remote DTO handling | `AgentId/HostId/SessionId` 路由 | 指定 session 可執行，且可回傳 `SessionState` | done |
| T22a | 將 `result_op` 留在 parent 層 | parent -> `ResultQueryService` | parent 已明確攔截 `RESULT_OP`，不進 session actor | done |
| T23 | 實作 `NetFxHostBackend.fs` | server-side adapter | 可透過新抽象呼叫 netfx host | done |

依賴：

- WP01
- WP02
- WP03

## WP06 Net10 Host Integration

目標：

把 `FAkka.FSI.Supervisor` / `FAkka.Proc.Supervisor` 納入正式 backend，且 out-of-proc host 建立流程一律經 `ProcSupervisor`。

子任務：

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| T24 | 新增 `ProcSupervisorClient.fs` | proc lifecycle adapter | 可 start/stop/query snapshot，且為唯一 out-of-proc provisioning 入口 | done |
| T25 | 新增 `FsiSupervisorClient.fs` | net10 host execution adapter | 可 execute/list sessions/get session | done |
| T26 | 實作 `Net10HostBackend.fs` | backend adapter | 可 execute/get state/health | done |
| T27 | 建立 host provisioning flow | `createHost(net10)` | 一律透過 `ProcSupervisor` 啟動 proc 並註冊 host | done |
| T28 | 建立 session provisioning flow | `createSession(net10)` | 可在同 host 建多 session | done |
| T28a | 禁止顯式建立 `inproc` host | control-plane validation | `create_fsi_host(inproc)` 會明確失敗 | done |

依賴：

- WP01
- WP02
- WP03

## WP07 Async Queue Refactor

目標：

把現有 `<asyncId, FsiResult option>` queue/caching 升級成 route-aware async job model。

子任務：

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| T29 | 重定義 async job type | `AsyncFsiJob` | job 含 route metadata | done |
| T30 | 重寫 queue worker | route-aware execution loop | FIFO 仍成立 | done |
| T31 | 完成 result linkage | async 完成可產出 `ResultId` | `fsi/async/{asyncId}` 可看到 `ResultId` | done |
| T32 | 保留舊 async tool 相容 | `execute_f_sharp_code_async` | 舊 client 可不改使用 | done |

依賴：

- WP03
- WP04/WP05/WP06

## WP08 MCP Tool / Resource Surface

目標：

把 control-plane、execution-plane、result-plane 暴露成 MCP 工具與資源。

子任務：

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| T33 | 切分 `McpFsiTools.fs` | `McpFsiTools`, `McpControlPlaneTools`, `McpResultTools` | control-plane tools 已搬到獨立檔案，`McpFsiTools` 不再承載 host/session 管理入口 | done |
| T34 | 新增 control-plane tools | register/create/list | agent/host/session 可管理，且 `create_fsi_host` 僅支援 `netfx/net10` | done |
| T35 | 新增 routed execution tools | explicit route tools | 已新增 `execute/evaluate/reset/get_state/add_search_path/reference_assembly` routed tools，可指定 `agentId/hostId/sessionId` | done |
| T36 | 新增 result tools | `get/list/query/compare` | 已新增 `get/list/query/compare` result tools，且支援 synthetic result materialization | done |
| T37 | 新增 resources | `fsi/hosts/*`, `fsi/results/*`, `fsi/path-mappings` | `fsi/agents/*`、`fsi/hosts/*`、`fsi/results/*`、`fsi/path-mappings` 已可讀 | done |
| T38 | 更新 `Program.fs` 註冊 | tool/resource wiring | server 啟動正常 | done |

依賴：

- WP03
- WP07

## WP09 Result Query Capability

目標：

把你提到的 quotation / result-set operations 納入可實作設計，並由 parent / supervisor 層協調。

子任務：

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| T39 | 新增 `ResultQueryTypes.fs` | query request/response types | `ResultQueryRequest/Response/Language/Kind/Materialization` 已可編譯 | done |
| T40 | 新增 `ResultQueryService.fs` | built-in ops | `exists/forall/map/filter/zip/diff/groupBy` 已可用 | done |
| T41 | 支援 `FSharpCode` query | server-side analysis execution | 可對 `ResultId seq` 執行 F# query string | done |
| T42 | 加入 result materialization policy | query 產出可轉新 `ResultId` 或 json | built-in query 已支援 `syntheticResult` materialization | done |
| T42a | parent-level result orchestration | host parent / server orchestration flow | result query 由 server-side `ResultQueryService` 協調，不進 session actor | done |

依賴：

- WP01
- WP03
- WP08

## WP10 Verification / Regression

目標：

建立最低驗收與回歸測試，避免新 control plane 破壞既有功能。

子任務：

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| T43 | 舊工具 smoke | default route regression tests | smoke 測試已覆蓋舊工具 execute/eval/restart/async 相容性 | done |
| T44 | multi-host smoke | 兩個 host 並行 | smoke 測試已驗證兩個 host 狀態不互相污染 | done |
| T45 | multi-session smoke | 同 host 兩個 session | smoke 測試已驗證同 host 不同 session state 隔離 | done |
| T46 | async job smoke | queue FIFO + result linkage | smoke 測試已驗證 FIFO 與 `asyncId -> ResultId` linkage | done |
| T47 | result query smoke | compare / forall / exists | smoke 測試已驗證 built-in result plane | done |
| T48 | `check.fsx` / DevLog / docs closeout | SOP evidence | 無 FAIL，且 `DEMO.md` / `DevLog` / notes 已同步 | done |

依賴：

- WP01-WP09

## 依賴圖

```text
WP01 -> WP02 -> WP03
WP03 -> WP04
WP03 -> WP05
WP03 -> WP06
WP04/WP05/WP06 -> WP07
WP03/WP07 -> WP08
WP01/WP03/WP08 -> WP09
WP01-WP09 -> WP10
```

## 建議實作順序

### Sprint A

1. WP01
2. WP02
3. WP03
4. WP04

交付條件：

- in-proc 路徑先跑起來
- 舊工具仍能工作

### Sprint B

1. WP05
2. WP06
3. WP07
4. WP08

交付條件：

- netfx/net10 雙 remote backend 都能路由
- MCP surface 完整

### Sprint C

1. WP09
2. WP10

交付條件：

- result plane 可用
- regression evidence 完整

## 完成定義

### SD 對應完成定義

1. `FsiResult` 與 `FsiExecutionRecord` 已分層
2. `IFsiExecutionBackend` 已落地
3. `Agent/Host/Session/AsyncJob/Result` 五個 registry 已落地
4. `netfx` host 多 session可用，且採 `actor per session`
5. `net10` host 可由 `FAkka.*` 跑起來，且建立流程一律經 `ProcSupervisor`
6. `ResultId` 可追 result
7. `query_fsi_results` 可做集合運算
8. `result_op` 由 parent / supervisor 協調，不進 session actor

### production-ready 最低門檻

1. 舊工具不破
2. 新工具可顯式 route
3. `create_fsi_host` 僅支援 `netfx/net10`

## 優化排程

這一段是本輪 self-use 與 client/E2E 驗證後，認為下一輪最值得優先收斂的項目。

| ID | 優化項 | 原因 | 建議產出 | 建議優先度 |
| --- | --- | --- | --- | --- |
| O1 | `.NET 10` session reset 正式化 | `Net10HostBackend.ResetSession` 仍受限於上游 supervisor contract，不能接受永久維持 stub | `FAkka.Fsi.Contracts` 增加 reset message，`FAkka.FSI.Supervisor` 與 `IFsiSupervisorClient` 接上，補 unit/integration/e2e | P0 |
| O2 | `McpClientHarness` repo 外重用體驗 | 外部 `.fsx` 重用時仍需顯式帶 `ModelContextProtocol.*` 與 logging dependencies | 提供 `samples/` 或 `demo-client` console app，降低外部 consumer bootstrap friction | P1 |
| O3 | routed execution onboarding | 非 default agent 走 explicit route 時，若未先建 host/session，使用者容易踩 ownership / missing session error | 補 clearer tool description、DEMO flow、可能的 helper tool / bootstrap workflow | P1 |
| O4 | `FSharpCode` result query 序列化策略 | 現在對不可直接序列化物件採 fallback，足夠可用但還不夠穩 | 補 richer serializer policy 與更多 result-shape regression tests | P1 |
| O5 | 真 out-of-proc net10 E2E | 目前 `.NET 10` backend 主要靠 fake supervisor/client 測試；真正跨 proc 仍值得補 | 增加可啟動 `FAkka.FSI.Supervisor` 的 integration/e2e suite | P2 |
4. out-of-proc host 建立流程一律經 `ProcSupervisor`
5. host/session health 可查
6. path mapping 可查
7. async queue 有 trace metadata
8. logs/check/doc 可追溯
