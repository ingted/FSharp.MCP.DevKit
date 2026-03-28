# BA

## 任務定位

本次 BA 不是在討論單一 bug fix，而是在決定 `fsharp-devkit` 要不要從「單一 session 的工具型 MCP server」提升成「可供多 agent 並行、安全使用的 execution platform」。

這代表本輪的 business value 不只來自補幾個 tool，而是來自三件事：

1. 降低 agent 使用成本
2. 提高多 agent / 多工作流並行時的穩定性
3. 建立 production-ready 的 backend 演進路徑，同時保留既有 client 相容性

## 利害關係人與需求視角

### 1. Agent / MCP client 使用者

需要的是：

1. 可預期的 tool 行為
2. 可觀測的 session / host 狀態
3. 長操作不被 timeout 誤殺
4. 發生錯誤時能快速定位，不必大量 bisect

### 2. Server 維護者

需要的是：

1. backend 結構清楚，不要把 transport、queue、session、host lifecycle 混在同一個 service
2. 新增 `.NET Framework` host 與 `.NET 10` host 時，不必重寫整層 MCP tool
3. 既有 client 不會因架構升級整批壞掉

### 3. 部署 / 維運者

需要的是：

1. 可以同時支援 in-proc 與 out-of-proc backend
2. path mapping、mount mapping、host health 都可查
3. 異常時能知道是 server 壞、host 壞、session 壞，還是 script 本身壞

## 問題分群

### A. 已確認的產品缺口

1. async diagnostics 太弱
2. session observability 不足
3. async contract 太薄
4. path / mount mapping 不可發現
5. 長操作的 async 覆蓋面不足
6. stdout / probe 可用性不足

### B. 已確認的架構缺口

1. 目前 `main` 是單一 in-proc `FsiService` 模型，無法自然支撐多 agent / 多 host / 多 session。
2. `20260319_001.async-merge` 雖已有 `net472 FsiHost + Akka remote`，但設計仍是「單一遠端 host 持有唯一 FSI session」，尚未支撐 host dictionary / session dictionary。
3. 若不引入 backend abstraction，in-proc 與 remote host 雙路徑會讓 `McpFsiTools.fs` 持續膨脹成混合 transport / execution / cache / orchestration 的 God object。

### C. 懷疑但未證實

1. 大量 `#` / `"` 是否在 agent tool-call JSON 轉換層造成系統性 escaping 問題。
2. 這一項目前不能當主需求，只能在後續 Test / QA 階段設計成驗證假說。

## 既有可重用能力

### 1. `main` 已有的可重用能力

1. `execute_f_sharp_code_async`
2. `fsi/async/{asyncId}` status resource / endpoint
3. async queue + cache 基本模型
4. 現有 MCP tool surface 與 backward-compatible client contract

### 2. `20260319_001.async-merge` 已有的可重用能力

1. `net472 FsiHost`
2. Akka remote actor transport
3. `Messages` transport-safe DTO 分層
4. server 透過 remote client adapter 呼叫遠端 host 的方向
5. 混合 runtime 部署與 service 化文件雛形

### 3. 這次真正新增的能力，不應與既有實作重工

1. agent registration
2. host dictionary
3. host kind 抽象（`inproc` / `netfx` / `net10`）
4. session dictionary
5. execution routing contract（`agentId + hostId + sessionId`）
6. 對舊 client 的相容策略

## Business Capability 決策

### Capability 1. Dual-backend support

保留兩套路徑：

1. `InProcBackend`
2. `RemoteHostBackend`

BA 判斷：

- 這是必要能力，不是過渡 workaround。
- 原因是使用者已明確要求既有 MCP client 不可被破壞；若直接只留 remote host，等於產品契約改版。

### Capability 2. Explicit multi-tenant execution model

新增顯式模型：

1. `agentId`
2. `hostId`
3. `sessionId`
4. `hostKind`

BA 判斷：

- 這是 production-ready 的核心能力。
- 若沒有這層，所謂多 agent 並行實際上只是共用單一 session，會互相污染 state。

### Capability 3. Backward compatibility layer

保留既有工具可直接跑，但對應到 default routing：

1. default agent
2. default host
3. default session

BA 判斷：

- 這是本輪必要需求，不是 nice-to-have。
- 因為使用者已明確要求現有 mcp client 能跑，且若要破壞相容性，必須先改 AGENTS / 契約文件。

### Capability 4. Host management as first-class feature

除了 execution tool，產品還需要 host 管理能力：

1. register agent
2. create host
3. list hosts
4. create session
5. list sessions
6. get workspace/path mapping
7. get host / session health

BA 判斷：

- 若只加新的 execute tool 而沒有 registry / observability，產品仍然只是 toy。

## 優先順序

### P0

1. backend abstraction
2. agent registration
3. host registry
4. session registry
5. backward compatibility strategy
6. async diagnostics 補強

### P1

1. `.NET 10` FSI host
2. host/session 資源查詢與 health model
3. path / mount mapping discoverability
4. async contract metadata 補強

### P2

1. explain-last-error 類能力
2. stdout/stderr capture 強化
3. async 版本的 load/reference/search-path 全面化

## 產品邊界與策略

### In Scope

1. 以 `origin/20260319_001.async-merge` 為基底延續，而不是在 `main` 上重做 remote host 能力。
2. 同時保留 in-proc backend 與 remote host backend。
3. 新增 `.NET 10` host 類型。
4. 新增 agent / host / session 三層 registry。
5. 修補 `Requirement.md` 描述的 confirmed issues，尤其是 diagnostics、observability、async contract。

### Out of Scope

1. 本輪不先淘汰 in-proc backend。
2. 本輪不先追求所有舊工具都全面 host-aware；先讓核心 session-bound tools 具備新 routing 能力。
3. 本輪不先處理所有歷史 build warning。

## 成功定義

### 對使用者

1. 不改現有 MCP client，也能繼續跑既有流程。
2. 新 client 可顯式指定 `agentId / hostId / sessionId`。
3. 可以同時存在多個 host，而且 host 可區分 `netfx` 與 `net10`。
4. 每個 host 下可有多個 session，執行請求可路由到指定 session。

### 對產品

1. backend 不再綁死單一 session 模型。
2. server 端可清楚區分 control plane 與 execution plane。
3. host/session 狀態、mapping、錯誤資訊可查。
4. 新能力是延續既有 async-merge 雛形，不是平行重做。

## BA 結論

本次任務的 business framing 應定義為：

1. 把 `fsharp-devkit` 從單機單 session 工具，升級成可支援多 agent / 多 host / 多 session 的 execution platform。
2. 技術路線不是推翻既有實作，而是：
   - 沿用 `main` 的 MCP tool 面
   - 沿用 `20260319_001.async-merge` 的 remote host 雛形
   - 在其上補 `registry + routing + observability + compatibility`
3. 因此下一步 SA 應聚焦：
   - 哪些結構可沿用
   - 哪些責任必須重切
   - 新的 control plane / execution plane 邊界如何劃分
