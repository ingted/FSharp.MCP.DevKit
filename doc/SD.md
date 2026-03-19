# SD

## 設計目標

- 統一 `FSharp.MCP.DevKit - Async` 的 FSI session backend。
- 保留 net472 `FsiHost` 作為實際 FSI session 執行者。
- 讓 sync tool、async queue、status polling 都落在同一個遠端 session 上。
- 修正 target framework 與 actor/message compile path。

## 設計決策

### 1. Backend 單一路徑

- `FsiHost` 維持 `net472`，持有唯一 FSI session。
- `Server` 維持 `net9.0;net10.0`，不再在 process 內自啟第二個 `FsiService`。
- `Server` 透過 Akka remote actor 對 `FsiHost` 發送 command request。
- `async queue` 只負責排程與快取，不直接持有 FSI session。

### 2. Remote Command DTO

- 在 `Messages` 專案新增可序列化的 remote command / response DTO。
- 不直接跨 Akka remote 傳 `obj option` 或 `FSharpDiagnostic[]`。
- response 使用 transport-safe DTO：
  - `Output`
  - `Errors`
  - `IsSuccess`
  - `ExecutionTimeMs`
  - `Diagnostics`（轉成純 record DTO）

### 3. Remote Client Adapter

- 在 `Server/McpFsiTools.fs` 建立 `RemoteFsiClient`，對上模擬既有 `PipeClient` 風格方法：
  - `ExecuteCode`
  - `EvaluateExpression`
  - `LoadScript`
  - `ParseAndCheck`
  - `ReferenceNugetPackage`
  - `ReferenceAssembly`
  - `AddSearchPath`
  - `Reset`
  - `Restart`
  - `GetState`
  - `IsServerAvailable`
- 這樣大部分 MCP tool 實作可維持既有 calling shape，不需要全面重寫工具層。

### 4. Async Queue 位置

- async queue 放在 `FsiMcpService`。
- queue item 至少包含：
  - `AsyncId`
  - `Code`
  - `Timeout`
  - `EnqueuedAt`
- queue worker 逐筆取出後，呼叫 `RemoteFsiClient.ExecuteCode` 到遠端 host。
- 執行結果轉回 `FsiResult` 後寫入 `AsyncFsiResultCache`。

### 5. Target Framework 策略

- `Core` 保留 shared API 與 `FsiService`，目前 target `netstandard2.0;net10.0`。
- `FsiActor.fs` 雖然實體檔案放在 `src/FSharp.MCP.DevKit.Core`，但 compile owner 改成 `FsiHost`，用 linked source 方式編入。
- Akka package reference 與 `Messages` project reference 只放在 `FsiHost`，避免 shared library 背上 host-only 依賴。
- `Server` 保持 `net9.0;net10.0`。
- `Messages` 保持 `netstandard2.0`。
- 其他 library 維持 target repo 現況的 multi-target，避免再把 `Async - 複製` 的 `NU1201` 問題帶回來。

### 6. Runtime Config 解析策略

- `FsiHost` 讀 `akka.conf` 改為從輸出目錄解析。
- `FsiMcpService` 讀 `akka.server.conf` 改為從輸出目錄解析。
- 目的不是美化程式，而是消除「從非輸出目錄啟動就找不到 Akka config」的實際部署風險。

## 元件責任

### `src/FSharp.MCP.DevKit.Messages`

- 定義 Akka remote transport DTO。
- 不持有 FSI session 邏輯。

### `src/FSharp.MCP.DevKit.Core`

- 提供 `FsiService`。
- 提供 async status shared model。
- 保存 `FsiActor.fs` 原始碼，由 `FsiHost` linked compile 後將 remote command 轉成對 `FsiService` 的實際呼叫。

### `src/FSharp.MCP.DevKit.FsiHost`

- 啟動 Akka actor system。
- 啟動單一 `fsiActor`。
- 使用 `akka.conf` 的固定 port 作為 server 端連入點。

### `src/FSharp.MCP.DevKit.Server`

- `FsiMcpService`：
  - 建立 actor system / actor selection
  - 封裝 remote client adapter
  - 維護 async queue / cache
- `Program.fs`：
  - MCP tool registration
  - `fsi/async/{asyncId}` resource
  - HTTP GET `/fsi/async/{asyncId}`

## 資料流

### Sync execution

1. MCP tool 進入 `FSharpInteractiveTools.ExecuteFSharpCode`
2. tool 取用 `FsiMcpService.GetClient()`
3. `RemoteFsiClient.ExecuteCode` 送出 Akka remote command
4. `FsiHost` 的 `FsiActor` 呼叫 `FsiService.ExecuteInteraction`
5. transport-safe response 回到 server
6. tool 回傳 output 或 formatted error

### Async execution

1. MCP tool `execute_f_sharp_code_async` 建立 `asyncId`
2. `FsiMcpService.EnqueueExecuteCode` 把 request 寫入 channel
3. background worker 依 FIFO 取出 request
4. worker 透過 `RemoteFsiClient.ExecuteCode` 呼叫遠端 host
5. result 寫入 `AsyncFsiResultCache`
6. MCP resource / HTTP endpoint 透過 `GetAsyncExecutionStatus` 查詢 cache

## 關鍵相容性修正

1. `remoteActorPath` 與 `FsiHost/akka.conf` 必須統一為同一個 port。
2. `EnqueueExecuteCode` 的 timeout 參數型別要收斂為 `TimeSpan`，不可留下 `TimeSpan option -> TimeSpan` 的錯誤指派。
3. `FsiActor.fs` 必須有明確 compile path；本輪選擇由 `FsiHost` linked compile，而不是把 Akka / Messages dependency 塞回 `Core`。
4. remote response 不可攜帶非 transport-safe 型別。
5. host / server 的 Akka config 不可再依賴 current working directory。

## 驗證策略

- 第一層：`dotnet build` with local restore workaround，確認 merge 後真正的編譯錯誤。
- 第二層：至少驗證下列操作可在同一個遠端 session 連續成功：
  - `ReferenceNugetPackage`
  - `ExecuteFSharpCode`
  - `execute_f_sharp_code_async`
  - `fsi/async/{asyncId}` polling
- 第三層：確認 `Reset` / `Restart` 後 state 變化合理。

## 已驗證結果

- Build：`dotnet build FSharp.MCP.DevKit.Async.sln -v minimal -p:NuGetAudit=false` 成功。
- Smoke：
  - net472 `FsiHost` 成功監聽 `akka.tcp://FsiExecutionSystem@localhost:8081`
  - sync execute / async enqueue / polling / eval / reset 全部成功
  - `ReferenceNugetPackage("Newtonsoft.Json, 13.0.3")` 成功，後續程式碼可使用該 package
- 補充判讀：
  - 先前用 `dotnet fsi #r FSharp.MCP.DevKit.dll` 的 smoke 失敗，是 ASP.NET Core app 直接被 FSI 載入時無法正確套用 `runtimeconfig/deps`，不是 merged backend 本身壞掉。

## 回退策略

- 若 remote command 泛化後導致大面積 regression：
  - 保留 `McpFsiTools.fs.bak` 作為比對基線
  - 先恢復 sync execute / async queue 核心路徑
  - 其餘低頻工具延後到下一個 WBS phase 收斂

## 2026-03-19 相依更新設計補充

### 7. Security-driven dependency refresh

- `Akka` 與 `Akka.Remote` 必須維持同版本升級，避免 remoting protocol / serialization 行為漂移。
- 本輪採「最小必要升級」策略：
  - 只升已被 audit 判定為 critical 的 direct dependency
  - 不同時重整 `NuGet.*` 與 `Paket.Core`，避免把 security fix 與相容性整理混成同一輪
- 驗證採兩層：
  - solution-level `dotnet build`：確認 restore + compile 可過且 `NU1904` 消失
  - project-level `dotnet list package --vulnerable --no-restore`：確認兩個 Akka consumer 專案都無 vulnerable packages
