# SA

## 任務摘要

- 目前工作 branch：`20260322_001.multi-agent-fsi-platform`
- 基底：`origin/20260319_001.async-merge`
- 本輪目標不是重做 mixed-runtime，而是在既有 `async-merge` 雛形之上，分析如何升級成：
  - dual-backend
  - 多 agent / 多 host / 多 session
  - 保留既有 MCP client 相容性
  - 補強 Requirement/BA 指出的 diagnostics / observability / contract 缺口

## 分析範圍

本次 SA 聚焦三條主線：

1. 現有 execution plane 現況
2. 現有 MCP/control plane 現況
3. 往多租戶、多 host execution platform 演進時，哪些結構可沿用、哪些責任必須重切

## 現況觀察

### A. 目前 branch 的 execution plane 已是單一 remote host 架構

- [McpFsiTools.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/McpFsiTools.fs) 內的 `FsiMcpService` 會：
  - 建立 `ActorSystem`
  - 連到固定 `remoteActorPath = akka.tcp://FsiExecutionSystem@localhost:8081/user/fsiActor`
  - 透過 `RemoteFsiClient` 發送 `FsiRemoteCommandRequest`
- [FsiHost/Program.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.FsiHost/Program.fs) 啟動 `FsiExecutionSystem` 並建立固定名稱 `fsiActor`
- [FsiActor.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.FsiHost/FsiActor.fs) 在 actor 建構時建立單一 `FsiService(config)`，並持有單一 FSI session

結論：

- 目前 branch 已不是 in-proc server 直接跑 FSI
- 但它仍是「單一 remote host + 單一 actor + 單一 FSI session」模型

### B. transport contract 已有跨 runtime 安全分層

- [McpActorMessages.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Messages/McpActorMessages.fs) 已將 transport-safe DTO 抽出：
  - `FsiRemoteCommandRequest`
  - `FsiRemoteResult`
  - `FsiRemoteCommandResponse`
- 目前 DTO 只承載：
  - `RequestId`
  - `CommandType`
  - `Payload`
  - `UsePackageTargets`
  - execution result / diagnostics

結論：

- 這層非常值得沿用
- 但它目前沒有任何 multi-tenant routing 欄位，例如：
  - `AgentId`
  - `HostId`
  - `SessionId`
  - `HostKind`

### C. async queue 已存在，但仍屬單 session 語意

- `FsiMcpService` 已有：
  - `AsyncFsiResultCache`
  - `Channel<AsyncFsiExecutionRequest>`
  - 單 worker FIFO processor
- queue item 目前只包含：
  - `AsyncId`
  - `Code`
  - `Timeout`
  - `EnqueuedAt`
- 查詢面目前是：
  - MCP resource `fsi/async/{asyncId}`
  - HTTP GET `/fsi/async/{asyncId}`

結論：

- async queue 與 polling model 已可沿用
- 但目前沒有辦法把 job 綁到某個 agent / host / session

### D. MCP/control plane 目前幾乎沒有租戶概念

- [Program.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/Program.fs) 目前提供：
  - `/mcp`
  - `fsiAsyncStatus` resource
  - `/fsi/async/{asyncId}`
  - `/healthz`
- 現有 tool surface 仍是假設「只有一個 FSI backend」
- `healthz` 只回：
  - `status`
  - `transport`
  - `isWindowsService`
  - `serviceName`

結論：

- control plane 目前只有 server process 本身健康狀態
- 沒有 host registry、session registry、mapping registry

### E. 與 `main` 的關係

- `main` 仍保留較新的 in-proc queue/resource 改動與 Requirement/BA 相關文件
- 本 branch 保留的是較成熟的 remote host mixed-runtime execution path
- 因此這次不是單純在某一條線上修 bug，而是要把：
  - `main` 的文件與問題意識
  - `async-merge` 的 remote host execution 雛形
  組合成新的正式架構

## 已確認可沿用的部分

### 1. Remote host 執行骨架

可沿用：

1. `FsiHost`
2. `FsiActor`
3. `Messages` transport DTO
4. `RemoteFsiClient`
5. async queue + async status polling

理由：

- 這些已初步驗證過 mixed-runtime execution 可行，不應在 `main` 上重做一遍

### 2. 現有 MCP tool surface

可沿用：

1. 現有 sync tools 名稱
2. `execute_f_sharp_code_async`
3. `fsi/async/{asyncId}` resource

理由：

- 使用者已明確要求現有 MCP client 要繼續可跑

## 已確認必須重切的責任邊界

### 1. `FsiMcpService` 目前責任過重

它現在同時負責：

1. Akka client 連線
2. remote execution client
3. async queue
4. async cache
5. default timeout
6. 對外提供 tool 使用入口

這代表：

- execution adapter
- queue orchestration
- lifecycle 管理
- control plane 查詢

都混在同一個類型裡。

### 2. 缺少 backend abstraction

目前 branch 只有 remote backend，main 則是 in-proc backend。  
如果要保留雙 backend，現在沒有任何正式抽象層可掛這兩條路徑。

### 3. 缺少 registry 分層

目前沒有獨立概念來管理：

1. agent
2. host
3. session

因此所有 execution 都被隱含地路由到：

- 固定 host
- 固定 actor
- 固定單一 session

### 4. host 與 session 生命周期未分離

目前 `FsiActor` 啟動即建立單一 `FsiService`，actor lifecycle 等同 session lifecycle。  
這對單 session 模型成立，但不適合多 session host。

## 問題定義

目前這條 branch 解的是：

- 如何把 MCP server 與 net472 FSI host 用 Akka 接起來

但本輪真正要解的是：

- 如何把它升級成一個多租戶 execution platform

具體差異在於：

1. 現況只有 execution path，沒有 tenancy model
2. 現況只有單一 host address，沒有 host inventory
3. 現況只有單一 session，沒有 session routing
4. 現況只有 async job cache，沒有 job 與 agent/host/session 的關聯
5. 現況沒有 `.NET 10` host 類型

## 根因判讀

1. `async-merge` 的設計出發點是把 mixed-runtime 跑通，不是解 multi-tenant 問題，所以自然收斂成單一 host / 單一 session。
2. 先前 Requirement 中提到的 diagnostics、observability、mapping 問題，本質上都是「control plane 不足」的症狀。
3. 若只在現有 `McpFsiTools.fs` 上繼續疊功能，而不切出 backend / registry / routing 邊界，複雜度會快速失控。

## 假設

1. 本輪仍以 `origin/20260319_001.async-merge` 為 execution-plane baseline。
2. 舊 client 必須能繼續使用既有 tool/resource 名稱。
3. 新的 multi-tenant 能力可以先以顯式 tool/resource 補入，不必立刻把所有舊工具全面改成顯式 routing。
4. `.NET 10` host 與 `net472` host 需要共用同一份高層 contract，而不是各自發展一套 MCP tool surface。

## 風險

1. 若 `AgentId / HostId / SessionId` 直接滲透到所有既有 tool 簽名，會造成大面積 contract churn。
2. 若 `FsiActor` 繼續維持「actor = session」，多 session host 會很難落地。
3. 若 dual-backend 沒有共同抽象，之後會出現：
   - 某些 tools 只支援 in-proc
   - 某些 tools 只支援 remote host
   - state / diagnostics 表現不一致
4. path mapping 若只做文件、不做可查詢能力，production ready 仍不足。

## 分析結論

### 結論 1

`origin/20260319_001.async-merge` 應保留作為 execution-plane baseline，不應拋棄重做。

### 結論 2

本輪的真正缺口不在 mixed-runtime execution，而在 control plane：

1. backend abstraction
2. agent registry
3. host registry
4. session registry
5. observability / mapping / diagnostics query surface

### 結論 3

下一步 SD 不應直接從「加哪些 tool」開始，而應先定義：

1. backend 介面
2. registry 模型
3. host / session lifecycle
4. 舊工具如何映射到 default routing

## 關聯追溯

- Requirement: [Requirement.md](/workspace/home/mcp/FSharp.MCP.DevKit/doc/Requirement.md)
- BA: [BA.md](/workspace/home/mcp/FSharp.MCP.DevKit/doc/BA.md)
- 基底 branch 既有實作：
  - [McpFsiTools.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/McpFsiTools.fs)
  - [McpActorMessages.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Messages/McpActorMessages.fs)
  - [FsiActor.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.FsiHost/FsiActor.fs)
  - [Program.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/Program.fs)
