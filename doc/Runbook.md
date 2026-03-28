# Runbook — FSharp.MCP.DevKit

> 本文件的目標讀者是「任何 LLM Agent」。讀完之後應能正確建立 host、session 並執行 F# 程式碼，無需查閱其他文件。

---

## 1. 系統概覽

FSharp.MCP.DevKit 是一個 MCP (Model Context Protocol) server，提供：

1. **F# Interactive (FSI) 執行環境** — 在隔離的 out-of-process host 中執行 F# 程式碼
2. **程式碼分析** — AST 解析、symbol 查詢、型別檢查
3. **檔案編輯** — 格式化、插入、取代、搬移程式碼
4. **NuGet 文件產生** — 套件 API 文件自動產生與搜尋
5. **結果查詢** — 對歷史執行結果做 filter/compare/diff

### 架構分層

```
MCP Client (Claude Code, etc.)
    |
    v
MCP Server (HTTP Streamable, port 15000)
    |
    +-- Control Plane -- Agent/Host/Session 註冊與路由
    +-- Execution Plane -- InProc / NetFx / Net10 三種 backend
    +-- Result Plane -- 執行結果儲存與查詢
```

### 連線資訊

| 項目 | 值 |
|------|----|
| MCP endpoint | `http://<HOST>:15000/mcp` |
| Transport | HTTP Streamable MCP |
| ProcSupervisor REST | `http://<HOST>:6001` |
| Health check | `http://<HOST>:15000/healthz` |

---

## 2. 快速開始（最小可行流程）

### 先理解路徑：agent container 路徑不等於 remote host container 路徑

這一點是目前 agent 最常踩到的坑。

- 你在 agent container 內看到的路徑，例如：
  - `/workspace/home/work/...`
- 不一定是 `fsharp-devkit` container 內 remote host 真正看得到的路徑。

目前實際部署通常是：

| 主體 | 看得到的路徑 |
|------|--------------|
| agent container | `/workspace/home/...` |
| host machine | `/home/sa/gemini4/...` |
| `fsharp-devkit` container | `/gemini4/...` 與 `/workspace/...` |

**結論：**
- `#I` / `#r` 用到的非 NuGet DLL 路徑，必須寫成 **remote host container 可見路徑**
- 不能直接把 agent 自己 container 裡的絕對路徑原樣送進 remote session

如果你不確定目前部署的 volume mapping，先看：
- docker service / compose / `docker inspect`
- 或詢問 operator

### Step 1：建立 FSI Host

```
create_fsi_host(
  agentId        = "claude",
  hostId         = "fsihost1",
  hostKind       = "net10",
  executablePath = "dotnet",
  arguments      = "exec --runtimeconfig /app/Akka.Proc.Supervisor.runtimeconfig.json --depsfile /app/Akka.Proc.Supervisor.deps.json /app/Akka.Proc.Supervisor.dll --mode procnode --systemname fsi-proc --host 10.28.112.140 --port 0 --supervisor akka.tcp://proc-system@10.28.112.140:8110/user/proc-supervisor --procid fsihost1"
)
```

**必須遵守的規則：**
- `--procid` 的值**必須**與 `hostId` 完全一致
- `--systemname` **必須**是 `fsi-proc`（不能是 `proc-system`，否則會導致 DData WriteMajority 阻塞）
- `--port 0` 表示自動分配 port
- `probeMessage` / `probeIntervalMs` 可省略；省略代表不啟用 active probe，不會阻止 host 建立

**最小建議：**
- 第一次排錯時，先省略 `probeMessage` / `probeIntervalMs`
- 先確認 host/session/execution 本身能通，再加 probe

### Step 2：建立 FSI Session

```
create_fsi_session(
  agentId   = "claude",
  hostId    = "fsihost1",
  sessionId = "s1"
)
```

等待回傳 `status: SessionReady` 再繼續。

### Step 3：執行 F# 程式碼

```
execute_f_sharp_code_routed(
  agentId        = "claude",
  hostId         = "fsihost1",
  sessionId      = "s1",
  code           = "printfn \"hello world\"",
  timeoutSeconds = 30
)
```

### Step 3A：長腳本不要先走 sync routed execute

如果你要送的是：
- 大型初始化腳本
- 會 `#I` / `#r` 多個 DLL 的腳本
- 會載資料、算圖、算指標、跑歷史資料的腳本
- 已知可能超過數十秒的 workload

請優先使用：

```
execute_f_sharp_code_async_routed(
  agentId   = "claude",
  hostId    = "fsihost1",
  sessionId = "s1",
  code      = "...long script..."
)
```

再輪詢：

```
resources/read("fsi/async/{asyncId}")
```

直到 `status = completed` / `isCompleted = true`，再用同一個 session 做：

```
evaluate_f_sharp_expression_routed(...)
```

**原因：**
- live deployment 已實測：長時間的 sync routed execute 仍可能在 `McpClientSystem <-> fsi-proc` 這段 ask 路徑上發生 `AskTimeoutException` / `EndpointDisassociatedException`
- 同類 workload 改走 async routed execute 時，host/session 維持健康，後續 evaluate 可正常銜接

**規則：**
- 短 snippet / quick probe：可用 sync routed execute
- 真實業務腳本 / 重 workload：**一律先 async**

### Step 4：如果要送入外部 `.fsx` 片段，先做這兩件事

1. 把 `#I` 改成 remote host container 真看得到的路徑
2. 若腳本只有為了本地/互動模式包了：
   - `#if INTERACTIVE`
   - `#endif`
   則在送入 remote host 前先去掉這一層包裝

這不是語法潔癖，而是因為 agent 最常直接把自己 container 裡的路徑送入 remote host，然後誤以為是 host/session 壞掉。

---

## 2.1 指定案例：執行 `generate_real_charts.inspect_930k_vs_30k.fsx` 的第 1~76 行

目標檔案：

`/workspace/home/work/coldfar-symbolics/experiments/generate_real_charts.inspect_930k_vs_30k.fsx`

這個案例的關鍵不是 host/session 建立，而是 **第 2 行的 `#I` 路徑**。

原始腳本開頭是：

```fsharp
#if INTERACTIVE
#I @"/workspace/home/work/sharftrade7/實驗/SharFTrade.Exp/bin/net10.0"
#r "PersistedConcurrentSortedList.dll"
#r "DynamicObj.dll"
...
#endif
```

在 agent container 內這可能成立，但在 deployed `fsharp-devkit` container 內，通常應改成：

```fsharp
#I @"/gemini4/work/sharftrade7/實驗/SharFTrade.Exp/bin/net10.0"
```

然後再把 `#if INTERACTIVE` / `#endif` 去掉後送入 remote session。

**正確順序：**
1. `register_fsi_agent`
2. `create_fsi_host`
3. `create_fsi_session`
4. 送入「已改路徑、已去掉 `#if INTERACTIVE/#endif`」的第 1~76 行
   - 如果這段腳本偏重，先用 `execute_f_sharp_code_async_routed`
   - 等 `fsi/async/{asyncId}` 完成後再繼續
5. 再 `evaluate_f_sharp_expression_routed` 取：
   - `cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c`

**如果你直接送原始 1~76 行，最常見結果是：**
- search path 不存在
- `DynamicObj.dll` 等非 NuGet DLL 找不到
- session 進入 faulted，後續都只剩模糊錯誤

**如果你已經改對路徑，但 workload 很重，下一個常見問題是：**
- sync routed execute timeout
- 連線 disassociate
- agent 誤判成 remote host/session 不穩

這時不要先重建 host。先改成 async routed execute，再用同一 session evaluate。

---

## 3. 完整 Tool 參考

### 3.1 Control Plane Tools

| Tool | 用途 | 必填參數 |
|------|------|----------|
| `register_fsi_agent` | 註冊 agent | `agentId` |
| `create_fsi_host` | 建立 out-of-proc FSI host | `agentId`, `hostKind`, `executablePath` |
| `create_fsi_session` | 在 host 上建立 session | `agentId`, `hostId` |
| `ensure_fsi_route` | 確保 agent/host/session 路由存在 | `agentId`, `displayName`, `hostId`, `sessionId`, `sessionName` |
| `list_fsi_hosts` | 列出 agent 的 hosts | `agentId` |
| `list_fsi_sessions` | 列出 host 的 sessions | `hostId` |
| `get_fsi_host_health` | 檢查 host 健康狀態 | `hostId` |
| `get_fsi_path_mappings` | 查詢路徑映射 | （可選 `agentId`, `hostId`） |
| `check_fsi_server_status` | 檢查 MCP server 狀態 | 無 |

### 3.2 Execution Tools（Routed — 指定 agent/host/session）

| Tool | 用途 |
|------|------|
| `execute_f_sharp_code_routed` | 執行 F# 程式碼 |
| `execute_f_sharp_code_async_routed` | 非同步執行，回傳 asyncId |
| `evaluate_f_sharp_expression_routed` | 評估表達式，回傳值與型別 |
| `add_search_path_routed` | 加入 `#I` 搜尋路徑 |
| `reference_assembly_routed` | 加入 `#r` 組件參照 |
| `reset_fsi_session_routed` | 重置 session（見第 6.3 節已知限制） |
| `get_fsi_state_routed` | 查詢 session 狀態（見第 6.4 節已知限制） |

### 3.3 Execution Tools（Default — 使用內建 default route）

| Tool | 用途 |
|------|------|
| `execute_f_sharp_code` | 執行 F# 程式碼（走 default-agent/default-host/default-session） |
| `execute_f_sharp_code_detailed` | 同上但回傳詳細錯誤 |
| `execute_f_sharp_code_async` | 非同步執行 |
| `evaluate_f_sharp_expression` | 評估表達式 |
| `parse_and_check_f_sharp_code` | 語法與型別檢查（不執行） |
| `reference_nu_get_package` | `#r "nuget: ..."` |
| `reference_assembly` | `#r` 組件參照 |
| `add_search_path` | `#I` 搜尋路徑 |
| `load_f_sharp_script` | `#load` 載入腳本 |
| `reset_fsi_session` | 重置 default session |
| `restart_fsi_session` | 重啟 default session（比 reset 更徹底） |
| `get_fsi_state` | 查詢 default session 狀態 |

### 3.4 Result Tools

| Tool | 用途 |
|------|------|
| `get_fsi_result` | 用 resultId 取得單筆結果 |
| `list_fsi_results` | 列出 agent 的執行結果 |
| `query_fsi_results` | 對結果集做 filter/map/exists/forall/zip/diff/groupBy |
| `compare_fsi_results` | 比較兩組結果集 |

### 3.5 Code Analysis Tools

| Tool | 用途 |
|------|------|
| `analyze_code_structure` | 分析 .fsx 檔案結構 |
| `parse_source_to_ast` | 解析為 AST |
| `get_all_symbols` | 列出所有 symbols |
| `get_symbol_at_position` | 定位 symbol |
| `get_symbol_signature_at_position` | 取得 symbol 簽名 |
| `what_is_at_position` | 快速描述某位置的 symbol |

### 3.6 File Editing Tools

| Tool | 用途 |
|------|------|
| `get_lines` | 讀取檔案指定行 |
| `count_lines` | 計算檔案行數 |
| `insert_code` | 插入程式碼（含 Fantomas 格式化） |
| `delete_lines` | 刪除指定行 |
| `replace_text_range` | 取代指定行範圍 |
| `move_code_block` | 搬移程式碼區塊 |
| `search_and_replace` | 文字搜尋取代 |
| `search_in_file` | 搜尋檔案內容（回傳行號） |
| `preview_code_injection` | 預覽插入結果（不寫入） |
| `format_file` | Fantomas 格式化整個檔案 |

### 3.7 Documentation Tools

| Tool | 用途 |
|------|------|
| `generate_package_documentation` | 產生 NuGet 套件 API 文件 |
| `generate_project_documentation` | 產生專案所有套件文件 |
| `search_documentation` | 搜尋已產生的文件 |
| `show_documentation_config` | 顯示文件設定 |
| `set_documentation_output_directory` | 設定文件輸出目錄 |
| `list_cached_packages` | 列出本地 NuGet cache |
| `show_package_info` | 顯示套件詳細資訊 |

### 3.8 其他

| Tool | 用途 |
|------|------|
| `kill_all` | 終止所有 MCP server 管理的程序 |

---

## 4. 程式碼執行注意事項

### 4.1 `#r` / `#I` 指令必須分段送入

**錯誤做法（容易導致 session 進入 Faulted 狀態）：**
```fsharp
// 一次送太多 #r 可能失敗，且錯誤訊息不明確
#I @"/some/path"
#r "A.dll"
#r "B.dll"    // <- 如果 B.dll 有依賴問題，整批失敗
#r "C.dll"
```

**正確做法：**
```fsharp
// Step 1: 先加搜尋路徑
#I @"/some/path"

// Step 2: 逐一載入，確認每個都成功
#r "A.dll"

// Step 3: 確認 A 成功後再載 B
#r "B.dll"
```

### 4.2 Session Faulted 後的恢復

當執行失敗時，session 會進入 `SessionFaulted` 狀態。後續在同一 session 執行會得到模糊的錯誤訊息：
> `"Operation could not be completed due to earlier error"`

**恢復方式：**
1. **建立新 session**（推薦）：`create_fsi_session` 建一個新的
2. **重置 session**：`reset_fsi_session_routed`

**補充：**
- 現在 server 端已會在 session registry 已知為 `SessionFaulted` 時，直接回清楚的 recovery 錯誤
- 錯誤中會附：
  - `RequestId`
  - `HostId`
  - `SessionId`
  - `Backend`
  - `ResultId`
  - `PreviousFailedResultId`

### 4.3 `#r` 載入 DLL 失敗的常見原因

1. **DLL 已被 host process 載入不同版本** — host process (`/app/*.dll`) 自帶許多 runtime DLL，若你的 DLL 與 host 版本衝突，`#r` 會靜默失敗
2. **缺少 transitive dependency** — 載入 `A.dll` 成功，但 `B.dll` 依賴 `A.dll` 的不同版本
3. **路徑對錯 container** — `#I` 用的是 agent container 路徑，不是 remote host container 路徑
4. **NuGet 套件相容性** — 並非所有 NuGet 套件都能在 FSI host 環境中載入

**建議策略：**
- 先測試用 `printfn "hello"` 確認 session 正常
- `#r` 用完整路徑而非只用檔名
- 先確認 `#I` 指向的是 remote host container 內存在的目錄
- 如果某個 DLL 持續失敗，跳過它，看是否能由其他 DLL 的 transitive dependency 帶入
- 每個 `#r` 單獨執行，立即確認結果

### 4.4 Timeout 參數

- `timeoutSeconds = 0` 或省略 → 使用預設值 30 秒
- 大型 NuGet 套件載入或複雜計算建議設 60~120 秒
- Timeout 是 server 端等待 backend 回應的時間，不是 FSI 執行的 wall clock

### 4.5 長時間腳本的建議模式

對長時間 workload，建議固定採用：

1. `create_fsi_host`
2. `create_fsi_session`
3. `execute_f_sharp_code_async_routed`
4. 輪詢 `fsi/async/{asyncId}`
5. `evaluate_f_sharp_expression_routed`

不要直接把長腳本塞給 `execute_f_sharp_code_routed` 然後等單次同步回覆。

### 4.6 `#if INTERACTIVE`

FSI host 預設定義 `INTERACTIVE` symbol。如果你的程式碼使用 `#if INTERACTIVE`，FSI 會執行該分支。

但對 LLM agent 而言，真正的風險不是 symbol 本身，而是：
- `#if INTERACTIVE` 常把 `#I` / `#r` 包在一起
- agent 會原樣搬運本地路徑進 remote host

**因此建議：**
- 若只是把本地腳本片段 replay 進 remote host/session，優先把 `#if INTERACTIVE/#endif` 去掉後再送入
- 保留真正需要的 `#I` / `#r`，但把路徑改成 remote host 可見路徑

---

## 5. 多 Host / 多 Session 策略

### 為什麼要多 Host

- 不同 host 有不同的 DLL 載入上下文，互不干擾
- 一個 host crash 不影響其他 host
- 可分配不同任務到不同 host

### 為什麼要多 Session

- 同一 host 內的多個 session 共用 DLL 載入上下文
- 但 session 的變數綁定 (bindings) 彼此隔離
- 適合同一套件環境下的多個獨立實驗

### 命名建議

```
agentId:   "claude"
hostId:    "fsihost1", "fsihost2", ...
sessionId: "s1", "s2", ...  或  "host1-session1", ...
```

---

## 6. 已知限制與陷阱

### 6.1 `create_fsi_host` 需要 caller 知道 container 內部細節

**問題：** 必須手動傳入 `/app/Akka.Proc.Supervisor.dll` 等路徑和完整 Akka address。
**緩解：** 使用本 Runbook 第 2 節的固定 arguments 模板。
**根本修復：** 應由 server 自動組裝 arguments（已知缺陷）。

### 6.2 `--systemname` 不能是 `proc-system`

**原因：** procnode 會加入 supervisor 的 Akka Cluster，導致 DData WriteMajority 阻塞。
**正確值：** `fsi-proc`（或任何非 `proc-system` 的名稱）。
**後果：** 如果用錯，第二個 host 會 timeout，且即使 kill 掉問題 procnode，DData stuck state 仍殘留，需重啟整個 container。

### 6.3 `reset_fsi_session_routed` / `get_fsi_state_routed` 已改為 direct dispatch

這兩條路現在已不再依賴 `Execute` 的假路徑。

- `reset_fsi_session_routed`
  - 直接走 backend `ResetSession`
- `get_fsi_state_routed`
  - 直接走 backend `GetSessionState`

所以這裡已不是已知 bug，而是已修正行為。

### 6.4 Session Faulted 無自動恢復

**問題：** 一旦 execution 失敗，`ExecutionRouter.RouteAndExecute` 會將 session 標記為 `SessionFaulted`，之後所有操作都可能失敗。
**緩解：** 建立新 session。
**根本修復：** 需要自動重試或允許在 Faulted session 上嘗試執行。

### 6.5 錯誤訊息過於模糊

**問題：** upstream 原始錯誤有時仍然模糊。
**影響：** Agent 無法診斷問題根因。
**現況：** server 端已補上主要上下文：
- `RequestId`
- `HostId`
- `SessionId`
- `Backend`
- `ResultId`
- faulted session 還會補 recovery hint

**仍建議：**
- 若要深究，查看 `list_fsi_results`
- 必要時配合 host/container logs

### 6.6 長時間 sync routed execute 仍非首選

**現況：**
- live deployment 已驗證 remote host/session 本身可用
- 但長時間 sync routed execute 仍可能在 `McpClientSystem <-> fsi-proc` 這段 ask 路徑上超時或 disassociate

**這代表：**
- 不是 volume path 問題
- 也不是 remote host/session capability 本身壞掉
- 而是目前同步 routed execution 對長 workload 的穩定性仍不足

**操作建議：**
- 長腳本一律優先 `execute_f_sharp_code_async_routed`
- async 完成後再 `evaluate_f_sharp_expression_routed`

### 6.7 ResultQuery 未實作

**狀態：** Net10HostBackend 和 InProcBackend 都回傳 `UnsupportedOperationException`。
**影響：** `query_fsi_results` 和 `compare_fsi_results` 的 `fsharpCode` language 模式可能無法使用。built-in query 應正常。

---

## 7. 部署與維運

### Docker 部署

```bash
# 使用 build.host.sh（--no-cache，完整 rebuild + 自動重啟服務）
cd /workspace/home/mcp/docker/FSharp.MCP.DevKit
bash build.host.sh

# 或手動建置（context root 必須是 /workspace/home）
cd /workspace/home
sudo docker build -f mcp/docker/FSharp.MCP.DevKit/Dockerfile -t fsharp-mcp-devkit:local .
```

### 環境變數

| 變數 | 用途 | 預設值 |
|------|------|--------|
| `FSI_PROC_SUPERVISOR_HOST` | Supervisor 綁定 IP | -- |
| `FSI_PROC_SUPERVISOR_PORT` | Akka port | `8110` |
| `FSI_PROC_SUPERVISOR_PATH` | 完整 Akka actor path | `akka.tcp://proc-system@<HOST>:8110/user/proc-supervisor` |
| `FSI_ENABLE_PROC_SUPERVISOR` | 啟用 ProcSupervisor | `true`（設 `0`/`false`/`no` 可關閉） |
| `FSI_ENABLE_REMOTE_CLIENT` | 啟用遠端 FSI client | `true` |
| `FSI_PROC_SUPERVISOR_TIMEOUT` | ProcSupervisor client ask timeout（秒） | `60` |

### 健康檢查

```bash
curl http://<HOST>:15000/healthz
```

回傳欄位：`status`, `transport`, `isWindowsService`, `serviceName`

注意：健康檢查只檢查 MCP server process 本身，不檢查 FSI host 的實際狀態。使用 `get_fsi_host_health` 檢查個別 host。

### 異常處理

| 症狀 | 可能原因 | 處理方式 |
|------|---------|---------|
| `AskTimeoutException` | host 建立時 `--systemname` 錯誤 | 確認用 `fsi-proc`，重啟 container |
| `Operation could not be completed due to earlier error` | Session faulted | 建立新 session |
| `Host was not found` | hostId 不存在 | 用 `list_fsi_hosts` 確認 |
| `UnsupportedOperationException` | 該 backend 不支援此操作 | 見第 6 節已知限制 |
| DLL `#r` / `#I` 失敗 | 版本衝突、缺少依賴、或路徑對錯 container | 先確認 remote host 可見路徑，再逐一載入 |
| ProcSupervisor snapshot not found | host process 未啟動或已終止 | 重建 host |

### 回滾

- 所有狀態為 in-memory，重啟 container 即清除全部
- DData 無 durable storage（無 LMDB），重啟即可清除 stuck state

---

## 8. 實測驗證結果（2026-03-27）

### 成功案例

| Host | Session | 測試內容 | 結果 |
|------|---------|---------|------|
| fsihost1 | host1-session1 | Binary Search Tree（DU + 遞迴） | OK |
| fsihost1 | host1-session2 | `#r "nuget: Newtonsoft.Json, 13.0.3"` + JSON | OK |
| fsihost1 | host1-session3 | `#if INTERACTIVE` + Maybe monad CE | OK |
| fsihost2 | host2-session1 | `Async.Parallel` + `Async.RunSynchronously` | OK |
| fsihost2 | host2-session2 | Active patterns + `List.map` | OK |
| fsihost2 | host2-session3 | `#r "nuget: Newtonsoft.Json, 13.0.3"` + Record 序列化 | OK |

### NuGet 套件相容性

- **可用：** `Newtonsoft.Json 13.0.3`（從 cache 快速載入）
- **已知失敗：** `FSharp.Data 6.4.0`、`MathNet.Numerics 5.0.0`
- 失敗不影響同一 session 的後續操作

---

## 9. Claude Code 設定

在 `~/.claude/settings.json` 中加入：

```json
{
  "mcpServers": {
    "fsharp-devkit": {
      "type": "http",
      "url": "http://10.28.112.140:15000/mcp"
    }
  }
}
```

或使用 CLI：

```bash
claude mcp add --transport http -s user fsharp-devkit http://10.28.112.140:15000/mcp
```

注意：`settings.json` 中手動加的 MCP server，`claude mcp list` 可能看不到。必須用 `claude mcp add` 註冊才會被 CLI 認到。
