# FSharp.MCP.DevKit.Tests

本文件用表格描述 `tests/` 內每個測試案例的測試意圖與大致流程。

說明：
- `loop 內準備` 只在測試本身有 polling / repeated probe / batch iteration 時填寫。
- `loop 內 post test op` 只在每次迴圈後有額外檢查或狀態更新時填寫。
- 沒有對應內容時以 `無` 表示。

## BackendSelectorTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Resolve maps InProcHost to InProc backend` | 建立只註冊 `InProc` 的 fake backend selector | 驗證 `InProcHost -> InProc` mapping | 呼叫 `selector.Resolve(InProcHost)` 並比對 `BackendKind` | 無 | 無 | 斷言回傳 backend 為 `InProc` | 無 |
| `Resolve maps NetFxHost to NetFxRemote backend` | 建立只註冊 `NetFxRemote` 的 fake backend selector | 驗證 `NetFxHost -> NetFxRemote` mapping | 呼叫 `selector.Resolve(NetFxHost)` | 無 | 無 | 斷言回傳 backend 為 `NetFxRemote` | 無 |
| `Resolve maps Net10Host to Net10Remote backend` | 建立只註冊 `Net10Remote` 的 fake backend selector | 驗證 `Net10Host -> Net10Remote` mapping | 呼叫 `selector.Resolve(Net10Host)` | 無 | 無 | 斷言回傳 backend 為 `Net10Remote` | 無 |
| `Resolve throws when no backend is registered for host kind` | 建立空的 selector | 驗證缺 backend 時會失敗而不是 silent fallback | 呼叫 `selector.Resolve(Net10Host)` 並捕捉例外 | 無 | 無 | 斷言 `InvalidOperationException` | 無 |

## BackendAdaptersTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `toFsiResult maps diagnostics and value from pipe response` | 組一個含 `diagnostics/value/executionTime` 的 `PipeResponse` | 驗證 pipe response 會被正確標準化成 `FsiResult` | 呼叫 `BackendAdapters.toFsiResult` | 無 | 無 | 斷言 diagnostics/value/executionTime 都被保留 | 無 |
| `inferRawErrorType returns None for successful response` | 組 `IsSuccess=true` 的 response | 驗證成功回應不應被標成 raw error | 呼叫 `inferRawErrorType` | 無 | 無 | 斷言結果為 `None` | 無 |
| `inferRawErrorType returns UnknownRemoteError for blank error text` | 組 `IsSuccess=false` 且 `Errors=""` 的 response | 驗證空錯誤訊息的 fallback 類型 | 呼叫 `inferRawErrorType` | 無 | 無 | 斷言結果為 `UnknownRemoteError` | 無 |
| `inferRawErrorType returns RemoteExecutionError for explicit error text` | 組 `IsSuccess=false` 且有 `Errors` 的 response | 驗證顯式錯誤訊息的分類 | 呼叫 `inferRawErrorType` | 無 | 無 | 斷言結果為 `RemoteExecutionError` | 無 |
| `toExecutionRecord preserves routing and backend metadata` | 組 `ExecutionRequest`、`FsiResult` 與 metadata | 驗證 record 產生時不會遺失 route/backend/result metadata | 呼叫 `toExecutionRecord` | 無 | 無 | 斷言 `AgentId/HostId/SessionId/BackendKind/RawErrorType/ResultId` | 無 |

## ControlPlaneTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `DefaultRouting creates default agent host and session when route is omitted` | 建立空的 `agent/host/session registry` | 驗證 implicit route 會自動補 default agent/host/session | 呼叫 `DefaultRouting.resolve ... None` | 無 | 無 | 斷言 default route 與 default records 都被建立 | 無 |
| `DefaultRouting returns explicit route when agent host and session are valid` | 預先註冊合法的 agent/host/session | 驗證顯式 route 不會被改寫 | 呼叫 `DefaultRouting.resolve ... (Some route)` | 無 | 無 | 斷言回傳 route 與輸入相同 | 無 |
| `DefaultRouting rejects route when host belongs to another agent` | 建立 agent 與跨 agent host/session 組合 | 驗證 ownership 檢查會擋錯誤 route | 呼叫 `DefaultRouting.resolve` 並捕捉例外 | 無 | 無 | 斷言拋出 `InvalidOperationException` | 無 |

## ExecutionRouterTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `ExecutionRouter persists default-route results and updates session metadata` | 建立 registries、`InProcBackend`、`ExecutionRouter` | 驗證 default routed execution 會寫入 `ResultRegistry/SessionRegistry` | 執行 `AddSearchPath` request | 無 | 無 | 斷言 result 被存下且 session metadata 更新 | 無 |
| `ExecutionRouter preserves explicit route and stores multiple execution records` | 建立 explicit agent/host/session 與 router | 驗證 explicit route 下多次 execution 都能被追蹤 | 先 `ExecuteCode` 再 `EvaluateExpression` | 無 | 無 | 斷言兩筆 result 均存在且 session `LastExecutionAt` 更新 | 無 |

## FsiMcpServiceTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `FsiMcpService executes through default routed in-proc path and stores results` | 建立 `FsiMcpService(enableRemoteClient=false)` | 驗證 service 主路徑會透過 routed in-proc backend 執行並存 result | 先 `ExecuteCode` 再 `EvaluateExpression` | 無 | 無 | 斷言 `ListSessionResults` 能看到 result | `Dispose service` |
| `FsiMcpService async queue completes and exposes status` | 建立 service；enqueue async code | 驗證 async queue 能完成且狀態含 metadata/resultId | enqueue 後輪詢 `GetAsyncExecutionStatus`，再同步 eval | 每輪讀一次 async status | 若未完成則 `Task.Delay(100)` 再重試 | 完成後斷言 `ResultId/AgentId/HostId/SessionId` | `Dispose service` |

## InProcBackendTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `InProcBackend isolates state between sessions` | 建立單一 `InProcBackend` 與兩條不同 session route | 驗證 session state 隔離 | 在 session A 定義值，分別在 A/B evaluate | 無 | 無 | 斷言 A 成功、B 失敗 | backend 由 GC 回收 |
| `InProcBackend reset clears session state` | 建立 backend 與單一 session route | 驗證 reset 會清掉 session binding | 先 define，再 eval，reset 後再 eval | 無 | 無 | 斷言 reset 前成功、reset 後失敗 | backend 由 GC 回收 |
| `InProcBackend returns execution metadata and session state` | 建立 backend 與單一 session route | 驗證 execution metadata 與 session snapshot | 執行 `AddSearchPath` 後讀 `GetSessionState` | 無 | 無 | 斷言 `BackendKind/ResultId/route/searchPaths/status` | backend 由 GC 回收 |

## McpControlPlaneToolsTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `McpControlPlaneTools register host session and health flow works` | 建立 fake `ProcSupervisorClient`、fake `FsiSupervisorClient`、`FsiMcpService` | 驗證 MCP control-plane tool surface 可串起 agent/host/session/health | 依序呼叫 `register/create/list/health` tools 並反序列化輸出 | 無 | 無 | 斷言 agent/host/session/health payload 正確 | `Dispose service` |
| `ControlPlaneResources expose registered host and session` | 建立同上 fake clients/service，先建立 agent/host/session | 驗證 control-plane resources 能正確讀到註冊狀態 | 呼叫 `ControlPlaneResources` 各 resource method | 無 | 無 | 斷言 agent/host/session/path mappings JSON 內容 | `Dispose service` |

## McpExecutionToolsTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `McpExecutionTools execute evaluate reset and async on explicit default route work` | 建立 `FsiMcpService(enableRemoteClient=false)`，先 bootstrap default route session | 驗證 explicit route MCP execution tools 可用，且 async/reset 行為一致 | 依序呼叫 routed execute/evaluate/add-path/get-state/async/reset/evaluate | 每輪讀 async status | 若未完成則 `Task.Delay(100)` 再輪詢 | 完成後斷言 async metadata 與 reset 後 state 變化 | `Dispose service` |

## McpResultToolsTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `McpResultTools get list query compare and resources work` | 建立 `FsiMcpService(enableRemoteClient=false)`，產生兩筆可比較 result | 驗證 result get/list/query/compare/resource/materialization 一次到位，且 `FSharpCode` query 可執行 | 先產生 result，呼叫 `get/list/query/compare`、built-in query、`FSharpCode` query tools，再呼叫 result resources | 無 | 無 | 斷言 built-in map/diff、`FSharpCode` materialized JSON、synthetic result 與 resources 內容 | `Dispose service` |

## McpClientAvailabilityTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `MCP client can ping server and discover core tools and resources` | 透過主專案 `McpClientHarness` 啟動真 server stdio client | 驗證主專案內建 MCP client 能直接打通 server 可用性 | 先 `Ping`，再列 `tools/resources/templates` | 無 | 無 | 斷言核心 tools 與 resource templates 均存在 | `DisposeAsync client` |
| `MCP client can read direct and templated resources` | 同上，建立真 client | 驗證 resource read path 可直接使用 | 讀 `worldtime` 與 `time/Asia-Taipei` | 無 | 無 | 斷言 JSON 內容含預期 timezone 欄位 | `DisposeAsync client` |

## McpClientSmokeTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Client smoke follows FSI bound-value persistence pattern` | 透過 `McpClientHarness` 啟真 client/server | 驗證互動式 binding 可跨 call 保留 | 先 `execute_f_sharp_code` 定義值，再 `evaluate_f_sharp_expression` | 無 | 無 | 斷言第二次可讀到第一次定義的值 | `DisposeAsync client` |
| `Client smoke follows implicit it pattern` | 同上 | 驗證 FSI `it` 行為 | 先執行單一表達式，再 evaluate `it` | 無 | 無 | 斷言 `it` 回傳最近表達式值 | `DisposeAsync client` |
| `Client smoke follows latest-shadowed-value pattern` | 同上 | 驗證 shadowing 後會讀到最新值 | 多次 `execute_f_sharp_code` 重定義相同名稱，再 evaluate | 無 | 無 | 斷言回傳最新 binding | `DisposeAsync client` |
| `Client smoke follows reset-clears-state pattern` | 同上 | 驗證 reset 真的清掉 session state | define -> evaluate -> `reset_fsi_session` -> evaluate | 無 | 無 | 斷言 reset 後舊 binding 不再可用 | `DisposeAsync client` |
| `Client smoke covers async status and result linkage` | 同上 | 驗證 async tool / status resource / result lookup 串起來 | 先 async execute，再輪詢 `fsi/async/{asyncId}`，最後查 `get_fsi_result` | 每輪讀一次 async status | 若未完成則 `Task.Delay(100)` 再輪詢 | 斷言 `ResultId` 與 result resource/tool 一致 | `DisposeAsync client` |
| `Client smoke covers multi-session isolation` | 同上，先 bootstrap default route 並建立兩個 session | 驗證 routed session 隔離 | 在 `session-a` / `session-b` 各自 define + evaluate，再讀 session resources | 無 | 無 | 斷言兩個 session 值互不污染 | `DisposeAsync client` |
| `Client smoke covers built-in result query` | 同上，先產生兩筆 result | 驗證 built-in result query 真能透過 MCP client 使用 | 讀 result list 後呼叫 `query_fsi_results(kind=exists)` | 無 | 無 | 斷言 built-in query 成功且輸出正確 | `DisposeAsync client` |
| `Client smoke covers fsharp-code result query` | 同上，先產生兩筆 result | 驗證 `FSharpCode` query 可在真 MCP client path 上執行 | 讀 result list 後呼叫 `query_fsi_results(language=fsharpCode)` | 無 | 無 | 斷言 `records1` query 回傳預期 JSON | `DisposeAsync client` |

## McpClientE2ETests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `MCP client E2E runner executes all smoke scenarios without failures` | 建立 scenario catalog，並對每個 scenario 各自起一個真 client/server | 驗證所有 client smoke 案例整體可回歸 | 迭代執行 catalog 內所有 scenario，收集失敗項 | 每輪起一個新 client，執行對應 smoke scenario | 若 scenario 失敗則收集名稱與錯誤訊息 | loop 結束後彙整 failures | 若有 failure 則整體 fail，否則結束 |

## DemoClientSmokeTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Demo client scenarios run successfully` | 先 build `examples/FSharp.MCP.DevKit.DemoClient`，再用 helper 以已編譯 DLL 啟動 scenario | 驗證 repo 對外示範用的 demo client 真的能透過真 MCP stdio 跑通核心 scenario | 依序執行 `discover`、`ensure-default-route`、`legacy-roundtrip`、`result-aggregation` | 每輪準備 scenario 名稱並啟動一個新的 demo client process | 每輪收集 `stdout/stderr/exitCode`；若失敗則帶原始輸出 fail fast | loop 結束後確認每個 scenario 都有對應 marker | process 自然結束，無額外清理 |

## McpSurfaceTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `FSharpInteractiveTools execute evaluate add-path and state use routed service` | 建立 `FsiMcpService(enableRemoteClient=false)` | 驗證舊工具 surface 仍走新的 routed service | 依序呼叫 `Execute/Evaluate/AddPath/GetState` | 無 | 無 | 斷言舊工具回傳值與 routed service 一致 | `Dispose service` |
| `FSharpInteractiveTools detailed error includes routed execution metadata` | 建立 service，送入必定失敗的 code | 驗證 detailed error 會帶 execution metadata | 呼叫 `ExecuteFSharpCodeDetailed` | 無 | 無 | 斷言訊息含 `BackendKind/SessionId` 等欄位 | `Dispose service` |
| `Fsi async status resource reflects async tool completion` | 建立 service，呼叫 async tool | 驗證 async MCP resource 會反映 async tool 完成狀態 | enqueue 後透過 `Program.FsiResources.AsyncStatus` 取 status | 每輪讀一次 async status | 若未完成則 `Task.Delay(100)` 再重試 | 斷言 `Exists/IsCompleted/ResultId/route metadata` | `Dispose service` |

## Net10HostBackendTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Net10HostBackend maps nuget and path operations into supervisor execution requests` | 建立 fake host registry、fake `IFsiSupervisorClient`、fake `IProcSupervisorClient` | 驗證 `.NET 10` backend 會把高階 operation 轉成 supervisor exec request | 執行 `ReferenceNuget` 與 `AddSearchPath` | 無 | 無 | 斷言 request 內 `Code/Refs/Loads/SessionId` 映射正確 | 無 |
| `Net10HostBackend maps session snapshot into SessionRecord` | 建立 fake host 與 fake session snapshot client | 驗證 supervisor session snapshot 會被轉為 `SessionRecord` | 呼叫 `GetSessionState` | 無 | 無 | 斷言 `Status/Refs/Loads/SearchPaths/Variables` 正確 | 無 |
| `Net10HostBackend health check and restart delegate to ProcSupervisor` | 建立 fake proc client 與 fake fsi client | 驗證 health/restart 由 `ProcSupervisor` 負責 | 呼叫 `HealthCheck` 與 `RestartHost` | 無 | 無 | 斷言 health snapshot 與 restart delegation 正確 | 無 |
| `Net10HostBackend reset session delegates to supervisor and returns success record` | 建立 fake host registry、fake reset-capable `IFsiSupervisorClient`、fake `IProcSupervisorClient` | 驗證 `.NET 10` backend 的 `ResetSession` 已接上 supervisor contract | 呼叫 `ResetSession` | 無 | 無 | 斷言 reset 請求發出且回傳 `FSI session reset` 成功 record | 無 |

## SmokeRegressionTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Smoke old tools remain compatible on default route` | 建立 in-proc `FsiMcpService` | 驗證 legacy tools 經過 routed execution 重構後仍相容 | execute -> evaluate -> reset -> async -> evaluate | poll async status | 若未完成則 `Task.Delay(100)` 再輪詢 | 斷言 binding persistence、reset 後清空、async linkage 正常 | `Dispose service` |
| `Smoke multi-host routed execution keeps host state isolated` | 建立 net10 fake service，註冊 agent，建立兩個 host 與同名 session | 驗證不同 host 不會互相污染 state | host-a / host-b 各自 execute + evaluate | 無 | 無 | 斷言 host-a=101、host-b=202 | `Dispose service` |
| `Smoke multi-session routed execution keeps session state isolated` | 建立 net10 fake service，註冊 agent，建立同 host 下兩個 session | 驗證不同 session 不會互相污染 state | session-a / session-b 各自 execute + evaluate | 無 | 無 | 斷言 session-a=11、session-b=22 | `Dispose service` |
| `Smoke net10 routed reset clears session state` | 建立 net10 fake service，註冊 agent，建立 host 與 session | 驗證 net10 routed reset 會清掉指定 session state | execute -> evaluate -> routed reset -> evaluate | 無 | 無 | 斷言 reset 前為 33，reset 後舊 binding 不再存在 | `Dispose service` |
| `Smoke async queue remains FIFO and links result ids` | 建立 in-proc `FsiMcpService` | 驗證 async queue 的先後順序與 result id linkage | enqueue 兩筆 mutate code，再 evaluate | poll status1/status2 | 若未完成則 `Task.Delay(100)` 再輪詢 | 斷言 counter=2 且兩筆都有 `ResultId` | `Dispose service` |
| `Smoke result queries support exists forall and compare` | 建立 in-proc `FsiMcpService` | 驗證 built-in result query 可做 exists/forall/compare | 建立兩筆 result 後依序 query | 無 | 無 | 斷言 query output 與 materialized data 正確 | `Dispose service` |

## NetFxHostBackendTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `NetFxHostBackend forwards execute requests with explicit route` | 建立 fake remote client 與 `NetFxHostBackend` | 驗證 netfx backend 會把 explicit route 送到 remote request | 執行一筆 `ExecuteCode` request | 無 | 無 | 斷言 remote command 的 `Route/CommandType/Payload` | 無 |
| `NetFxHostBackend maps remote session snapshot into SessionRecord` | 建立 fake remote 回傳 session state | 驗證 remote session snapshot 會被轉成 `SessionRecord` | 呼叫 `GetSessionState` | 無 | 無 | 斷言 `Status/Refs/Loads/SearchPaths/Variables` | 無 |
| `NetFxHostBackend maps result query and host commands to parent-level remote commands` | 建立 fake remote client | 驗證 `ResultQuery/RestartHost/HealthCheck` 走 parent-level command | 依序送 `ResultQuery`、`RestartHost`、`HealthCheck` | 無 | 無 | 斷言 command type 對應 `RESULT_OP/RESTART/PING` | 無 |
| `NetFxHostBackend reset session uses RESET command with target route` | 建立 fake remote client | 驗證 `ResetSession` 不會丟 route | 呼叫 `ResetSession` | 無 | 無 | 斷言送出的 command type 為 `RESET` 且 route 正確 | 無 |

## ProvisioningServicesTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `HostProvisioningService rejects explicit inproc host creation` | 建立 registries 與 fake proc client | 驗證 explicit provisioning 不允許 `InProcHost` | 呼叫 `CreateHost(... InProcHost ...)` 並捕捉例外 | 無 | 無 | 斷言拋出 `InvalidOperationException` | 無 |
| `HostProvisioningService starts proc and stores ready net10 host` | 建立 fake proc client 會回 ready snapshot | 驗證 `CreateHost` 會啟動 proc 並更新 registry | 呼叫 `CreateHost(... Net10Host ...)` | 無 | 無 | 斷言 host registry 存到 ready host 與 proc metadata | 無 |
| `SessionProvisioningService bootstraps missing session through backend execute` | 建立 fake backend，第一次查 session 回 `Missing`，之後回 `Ready` | 驗證缺 session 時會先 bootstrap 再 hydration | 呼叫 `CreateSession` | 無 | 無 | 斷言 backend execute 被呼叫、session registry 被更新 | 無 |

## RemoteMessageContractTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `FsiRemoteCommandRequest carries route and timeout` | 建立完整 `FsiRemoteCommandRequest` | 驗證 remote request DTO 欄位完整 | 直接檢查 DTO 屬性值 | 無 | 無 | 斷言 `Route/TimeoutMs/UsePackageTargets` | 無 |
| `FsiRemoteResult carries value and raw error type` | 建立完整 `FsiRemoteResult` | 驗證 remote result DTO 能承載 value/raw error type | 直接檢查 DTO 屬性值 | 無 | 無 | 斷言 `Value/RawErrorType/ExecutionTimeMs` | 無 |
| `FsiRemoteCommandResponse carries host and session ids` | 建立完整 `FsiRemoteCommandResponse` | 驗證 remote response DTO 有 host/session metadata | 直接檢查 DTO 屬性值 | 無 | 無 | 斷言 `HostId/SessionId/SessionState` | 無 |

## SmartSymbolDetectionServiceTests.fs

| 測試 | 前準備 | 測試目的 | 測試方法 | loop 內準備 | loop 內 post test op | post loop op | end test/clean |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Basic test placeholder` | 無 | 保留最小 smoke placeholder，確認測試專案基本可執行 | 直接 `Assert.True(true)` | 無 | 無 | 無 | 無 |
