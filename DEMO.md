# DEMO

本文件示範如何把 `FSharp.MCP.DevKit` 當成一個可供 agent 長時間互動、查狀態、做結果聚合的 MCP server 來使用。

重點不是單次 `execute_f_sharp_code`，而是：

1. 註冊 agent
2. 建立或選擇 host / session
3. 多次執行互動
4. 用 resources 查狀態
5. 用 result tools 做 aggregation

## 功能地圖

| 類別 | 代表 tool / resource | 用途 |
| --- | --- | --- |
| Legacy FSI | `execute_f_sharp_code` `evaluate_f_sharp_expression` `execute_f_sharp_code_async` `reset_fsi_session` `get_fsi_state` | 舊 client 相容與單路徑快速互動 |
| Explicit Routed Execution | `execute_f_sharp_code_routed` `execute_f_sharp_code_async_routed` `evaluate_f_sharp_expression_routed` `add_search_path_routed` `reference_assembly_routed` `reset_fsi_session_routed` `get_fsi_state_routed` | 顯式指定 `agentId/hostId/sessionId` |
| Control Plane | `register_fsi_agent` `create_fsi_host` `list_fsi_hosts` `create_fsi_session` `list_fsi_sessions` `get_fsi_host_health` `get_fsi_path_mappings` | 管理 agent / host / session / health / mappings |
| Result Plane | `get_fsi_result` `list_fsi_results` `query_fsi_results` `compare_fsi_results` | 查詢歷史結果、聚合、多集合比較 |
| Resources | `fsi/async/{asyncId}` `fsi/agents/{agentId}` `fsi/hosts/{hostId}` `fsi/hosts/{hostId}/sessions/{sessionId}` `fsi/results/{resultId}` | 查 async 狀態、agent/host/session/result snapshot |
| Documentation | `generate_package_documentation` `generate_project_documentation` `list_cached_packages` `show_package_info` `search_documentation` `show_documentation_config` `set_documentation_output_directory` | 產出與查詢 NuGet / project 文件 |
| Parse / Code Editing | `parse_and_check_fsharp_code`、`parse_source_to_ast`、`analyze_code_structure`、`preview_code_injection` | parse/check 與 code-editing 前置分析 |

## Demo 0: 直接跑 demo client

如果你要先用真 MCP stdio client 快速驗證 server，而不是自己手打 MCP request，先 build demo client：

```bash
dotnet build examples/FSharp.MCP.DevKit.DemoClient/FSharp.MCP.DevKit.DemoClient.fsproj -m:1
```

然後直接跑已 build 的 DLL：

```bash
dotnet examples/FSharp.MCP.DevKit.DemoClient/bin/Debug/net10.0/FSharp.MCP.DevKit.DemoClient.dll discover
dotnet examples/FSharp.MCP.DevKit.DemoClient/bin/Debug/net10.0/FSharp.MCP.DevKit.DemoClient.dll legacy-roundtrip
dotnet examples/FSharp.MCP.DevKit.DemoClient/bin/Debug/net10.0/FSharp.MCP.DevKit.DemoClient.dll ensure-default-route
dotnet examples/FSharp.MCP.DevKit.DemoClient/bin/Debug/net10.0/FSharp.MCP.DevKit.DemoClient.dll async-roundtrip
dotnet examples/FSharp.MCP.DevKit.DemoClient/bin/Debug/net10.0/FSharp.MCP.DevKit.DemoClient.dll result-aggregation
```

這五個 scenario 現在都會真的透過 `McpClientHarness -> stdio MCP transport -> server tools/resources` 跑，不是 direct call mock。

## Demo 1: 最小互動

適合舊 client 或先確認 server 是否能正常跑。

Prompt 範例：

```text
在 FSI 定義 let x = 41，再算 x + 1，最後把 FSI state 給我。
```

預期 tool flow：

1. `execute_f_sharp_code(code = "let x = 41")`
2. `evaluate_f_sharp_expression(expression = "x + 1")`
3. `get_fsi_state()`

## Demo 2: 正規 agent / routed execution

適合多 agent / 多 session 的正式用法。

Prompt 範例：

```text
註冊一個 agent 叫 demo-agent，之後都用它。
在 default-host/default-session 裡連做三次互動：
1. let sample = 40
2. let sample = sample + 1
3. 評估 sample
```

預期 tool flow：

1. `register_fsi_agent(agentId = "demo-agent", displayName = "Demo Agent")`
2. `ensure_fsi_route(agentId = "demo-agent", displayName = "Demo Agent", hostId = "default-host", sessionId = "default-session", sessionName = "")`
3. `execute_f_sharp_code_routed(agentId = "demo-agent", hostId = "default-host", sessionId = "default-session", code = "let sample = 40")`
4. `execute_f_sharp_code_routed(agentId = "demo-agent", hostId = "default-host", sessionId = "default-session", code = "let sample = sample + 1")`
5. `evaluate_f_sharp_expression_routed(agentId = "demo-agent", hostId = "default-host", sessionId = "default-session", expression = "sample")`

補充：

- 如果不是沿用 `default-host/default-session`，就先走：
  - `create_fsi_host`
  - `create_fsi_session`
- 如果只是要 bootstrap `default-host/default-session` 或一個已存在的 host/session，先走 `ensure_fsi_route`
- out-of-proc host 目前只支援 `netfx` / `net10`

## Demo 3: Async + polling

Prompt 範例：

```text
把一段可能比較久的初始化改走 async，拿到 asyncId 後持續輪詢，完成時把對應 result 也拿回來。
```

預期 tool / resource flow：

1. `execute_f_sharp_code_async(...)` 或 `execute_f_sharp_code_async_routed(...)`
2. 讀 `fsi/async/{asyncId}`
3. 若 `isCompleted = false`，持續輪詢
4. 完成後取出 `resultId`
5. `get_fsi_result(agentId, resultId)` 或讀 `fsi/results/{resultId}`

推薦 agent prompt：

```text
用 async 方式執行初始化。不要直接猜結果，拿到 asyncId 後輪詢 fsi/async/{asyncId}，直到 isCompleted=true，再把 resultId 對應的結果摘要給我。
```

## Demo 4: 多 session 隔離

Prompt 範例：

```text
在同一個 host 下建立 session-a 和 session-b。
在 session-a 設定 let v = 11，在 session-b 設定 let v = 22，最後各自讀回 v。
```

預期 flow：

1. `create_fsi_session(agentId, hostId, sessionId = "session-a")`
2. `create_fsi_session(agentId, hostId, sessionId = "session-b")`
3. 兩次 `execute_f_sharp_code_routed(...)`
4. 兩次 `evaluate_f_sharp_expression_routed(...)`
5. 讀 `fsi/hosts/{hostId}/sessions/{sessionId}` 確認 session snapshot

## Demo 5: 聚合多次互動結果

這是目前最值得 agent 用的能力。

Prompt 範例：

```text
對同一個變數做三次實驗，然後把最後三筆 result 做兩種 aggregation：
1. built-in exists，確認是否有結果包含 42
2. fsharpCode，把每筆 result 的 Value 抽出成 list
```

建議 flow：

1. 多次 execute / evaluate，累積 `ResultId`
2. `list_fsi_results(agentId)` 或讀 `fsi/agents/{agentId}/results`
3. 整理出要比較的 `resultId` 集合
4. 先做 built-in query：

```text
query_fsi_results(
  agentId = "...",
  kind = "exists",
  primaryResultIds = "id1,id2,id3",
  queryText = "valueContains:42"
)
```

5. 再做 `FSharpCode` query：

```text
query_fsi_results(
  agentId = "...",
  kind = "map",
  language = "fsharpCode",
  primaryResultIds = "id1,id2,id3",
  queryText = "records1 |> Seq.map (fun record -> record.Result.Value |> Option.defaultValue \"\") |> Seq.toList"
)
```

重點：

- `records1` / `records2`、`primaryRecords` / `secondaryRecords` 會在 server-side query session 中自動可用
- 如果 `queryText` 是 `fun records1 records2 -> ...`，server 會自動套用兩個參數

## Demo 6: 讀 control-plane 與 result-plane resources

Prompt 範例：

```text
不要只看工具回傳，請把 agent、host、session、result 的 resources 都讀出來做交叉確認。
```

可讀的關鍵 resources：

- `fsi/agents/{agentId}`
- `fsi/hosts/{hostId}`
- `fsi/hosts/{hostId}/sessions`
- `fsi/hosts/{hostId}/sessions/{sessionId}`
- `fsi/agents/{agentId}/results`
- `fsi/hosts/{hostId}/sessions/{sessionId}/results`
- `fsi/results/{resultId}`
- `fsi/path-mappings`

## Demo 7: 文件 / parse-check / code-editing

除了 FSI execution，這個 server 也能做文件與分析。

Prompt 範例：

```text
先 parse and check 這段 F# code；如果沒大錯，再分析某個 .fsx 的結構；最後幫我產生某個 NuGet package 的文件。
```

對應工具：

1. `parse_and_check_fsharp_code`
2. `analyze_code_structure`
3. `generate_package_documentation`
4. `search_documentation`

## Agent Prompt 範本

### 範本 A：長互動 + async + agg

```text
請用 demo-agent 在 default-host/default-session 做三次互動：
1. 定義一個值
2. 修改它
3. 用 async 再做一次衍生初始化

完成後：
1. 讀 fsi/async/{asyncId} 直到完成
2. 從 fsi/agents/demo-agent/results 收集這次流程的 result ids
3. 先用 built-in query 判斷是否全部成功
4. 再用 fsharpCode query 把每筆 result 的 Value 抽成 list
5. 最後讀 session resource 做交叉確認
```

### 範本 B：多 session 比較

```text
請建立兩個 session，對同一段程式碼在 session-a / session-b 各跑一次。
之後比較兩邊最近兩筆結果，若有差異請列出 diff，若沒有就回報一致。
```

## 實務注意事項

1. 對非 default agent 做 routed execution 前，先確保 host / session 已存在。
2. 若只是要進入 legacy default route 或已存在 route，先用 `ensure_fsi_route`；若要新建 out-of-proc host，必須先 `create_fsi_host`。
3. `execute_f_sharp_code_async` 最佳流程是 tool -> `fsi/async/{asyncId}` polling -> `resultId`。
4. `FSharpCode` query 是 server-side 受控 FSI session，不是直接傳 quotation object。
5. 若你要在 repo 外重用 client，優先用 `examples/FSharp.MCP.DevKit.DemoClient` 或正式 app 參考專案，不要從裸 `.fsx` + 少量 `#r` 開始；真 MCP client 對 transport/相依/序列化的要求比 direct call 高。
