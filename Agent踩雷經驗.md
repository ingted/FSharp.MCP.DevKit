# Agent 踩雷經驗

測試日期：2026-04-18
測試範圍：FSharp DevKit Local MCP tools

以下是實測時遇到的 bug、疑似 bug、或新手很容易誤用的地方。每項都附上現象與暫時 workaround，方便後續排修。

## P0：`insert_code` 會覆寫整個檔案

工具：

```text
insert_code
```

測試檔：

```text
G:\PulseTrade.fs\Libs\FSharp.MCP.DevKit\tmp\agent_mcp_scratch.fsx
```

現象：

1. 原檔有 14 行。
2. `insert_code` 在 line 14 插入時回：`Invalid line number 14. File has 0 lines.`
3. 改用 line 1 插入 `// inserted header` 時，工具回成功。
4. 事後用 shell `Get-Content` 看檔案，整個檔案只剩：

```fsharp
// inserted header
```

影響：

這是高風險資料毀損型問題。Agent 若在專案原始碼上使用，會把整檔覆寫掉。

暫時 workaround：

- 不要使用 `insert_code`。
- 新增程式碼改用安全的 patch 工具，或先用 `replace_text_range` 針對明確範圍替換。
- 若一定要測，僅能在 scratch file 上測。

## P1：`preview_code_injection` 對存在檔案回報 0 lines

工具：

```text
preview_code_injection
```

現象：

同一個檔案 `count_lines` 回 14 lines，`get_lines` 也能讀到內容，但 `preview_code_injection` 回：

```text
Error: Invalid line number 15. File has 0 lines.
Error: Invalid line number 14. File has 0 lines.
```

影響：

preview 無法作為 `insert_code` 的安全檢查，而且與讀檔工具的 line count 不一致。

暫時 workaround：

- 不要用 `preview_code_injection` 判斷可插入位置。
- 用 `count_lines` + `get_lines` 取代。

## P1：`analyze_code_structure` 小檔案 timeout

工具：

```text
analyze_code_structure
```

現象：

14 行 `.fsx` 小檔，連續兩次都在 30 秒 timeout：

```text
Error analyzing file: Timeout after 30.00 seconds
```

影響：

Agent 不能依賴它做快速結構分析。

暫時 workaround：

- 用 `get_all_symbols`。
- 或用 `get_symbol_at_position`、`get_symbol_signature_at_position`、`what_is_at_position` 做局部查詢。

## P1：`parse_and_check_f_sharp_code` 與 `parse_source_to_ast` timeout

工具：

```text
parse_and_check_f_sharp_code
parse_source_to_ast
```

現象：

傳入很小的 F# source literal，兩個工具都在 30 秒 timeout：

```text
Error parsing code: Timeout after 30.00 seconds
Parse failed: Timeout after 30.00 seconds
```

同一份 source 用 `get_all_symbols` 只花約 1.6 秒，position-based symbol tools 也正常。

影響：

目前 parse/check/AST 這條路不適合放在新手 SOP 或自動化主路徑。

暫時 workaround：

- 靜態資訊先用 symbol tools。
- 真正型別驗證可暫時用 `execute_f_sharp_code` 或 `execute_f_sharp_code_routed` 讓 FSI 編譯。

## P1：`ensure_fsi_route` generic error

工具：

```text
ensure_fsi_route
```

現象：

先 `register_fsi_agent` 成功，接著兩種呼叫都失敗且只有 generic error：

```text
ensure_fsi_route(agentId="newbie-agent-20260418", hostId="", sessionId="newbie-session")
ensure_fsi_route(agentId="newbie-agent-20260418", hostId="newbie-host", sessionId="newbie-session")
```

回傳：

```text
An error occurred invoking 'ensure_fsi_route'.
```

影響：

新 Agent 照工具描述使用會卡住，且錯誤訊息不足以判斷是 host 不存在、agent route 缺省、還是內部例外。

暫時 workaround：

- 用 `list_fsi_hosts("default-agent")` 找 `default-host`。
- 用 `create_fsi_session("default-agent", "default-host", "<session>")` 建 routed session。

## P2：`get_fsi_state` / `get_fsi_state_routed` 不列出 variables

工具：

```text
get_fsi_state
get_fsi_state_routed
```

現象：

已執行：

```fsharp
let newbieValue = 42
let routedValue = 15
```

而且 `evaluate_f_sharp_expression` 可以讀到 binding，但 state 回：

```text
Variables: (none)
```

search paths、refs、loads 會出現，但 variables 不出現。

影響：

Agent 不能用 state 裡的 variables 判斷 FSI 目前有哪些 binding。

暫時 workaround：

- 用 `evaluate_f_sharp_expression` probe 特定 binding。
- 或把重要 binding/resultId 自己記在工作筆記裡。

## P2：排程中的語法錯誤會讓 session faulted

工具：

```text
schedule_f_sharp_code_routed
process_next_due_schedule_445026308785
```

現象：

排程執行 invalid F#：

```fsharp
let broken =
```

第一次排程失敗後，同一 session 後續排程直接回：

```text
Session 'newbie-routed-session' on host 'default-host' is in Faulted state due to an earlier execution failure.
Call reset_fsi_session_routed or create_fsi_session to recover.
PreviousFailedResultId: ...
RawErrorType: SessionFaulted
```

影響：

這可能是設計行為，但新手很容易誤以為 scheduler 壞了。

暫時 workaround：

- 每次 failed execution 後先 `reset_fsi_session_routed`。
- 或為風險程式碼建立一次性 session。

## P2：`seal_session_output` archive event count 與 live events 不一致

工具：

```text
get_session_output_events
seal_session_output
get_session_output_archive
get_archived_session_output_events
```

現象：

`get_session_output_events` 在 seal 前可看到 `newbie-routed-session` sequence 1 到 9，共多筆 stdout/stderr。

呼叫 `seal_session_output` 後回：

```json
{"eventCount":1,"maxSequenceNo":9}
```

接著 `get_archived_session_output_events` 只看到 sequence 9 那一筆。

影響：

若預期 seal 會封存目前 live cache 全部事件，這個結果不符合直覺。也可能是 reset 後只封存目前 epoch，但 live output API 又看得到舊 events，語意需要釐清。

暫時 workaround：

- 需要完整 output 時，先用 `get_session_output_events(afterSequenceNo=0)` 抓回來。
- 要封存完整 session，優先用 `unregister_fsi_session` 的行為再交叉驗證。

## P2：WinAgent import 沒保留 envelope event timestamp

工具：

```text
import_winagent_execution_envelope
get_session_output_events
```

現象：

envelope 裡的 output event：

```json
"TimestampUtc":"2026-04-16T10:00:01Z"
```

匯入後 `get_session_output_events` 顯示 timestamp 是匯入當下：

```text
2026-04-18T03:52:39Z
```

影響：

若 downstream 依 output event 時間排序，會把 WinAgent 歷史事件看成匯入時間。

暫時 workaround：

- 用 execution record 的 `startedAt/completedAt` 判斷原始時間。
- 若需要事件級時間，後續修復應保留 envelope `TimestampUtc`。

## P2：`create_fsi_host` 對錯誤 host 設定只回 generic error

工具：

```text
create_fsi_host
```

測試：

```text
hostKind = net10
executablePath = dotnet
arguments = --version
```

現象：

只回：

```text
An error occurred invoking 'create_fsi_host'.
```

這個設定本來就不是合法 ProcSupervisor host，但錯誤訊息沒有提示「期待 procnode/supervisor protocol」或 stderr。

影響：

新手會把它當 process launcher，用錯後不知道怎麼修。

暫時 workaround：

- 把 `create_fsi_host` 視為進階部署工具。
- 使用前先準備 netfx/net10 ProcSupervisor host 與正確 arguments。

## P3：Documentation project generation 不會自動 restore package

工具：

```text
generate_project_documentation
```

現象：

對 `FSharp.MCP.DevKit.Core.fsproj` 產生 project docs 時，工具可成功完成 summary，但 5 個 package 都 failed：

```text
Package '<name>' not found in local NuGet cache
```

影響：

這不是 crash，但新手可能以為 project documentation 已完整產生。實際上成功數是 0。

暫時 workaround：

- 先 restore/build 專案，或用 `reference_nu_get_package`/其他方式把套件放進 MCP server 使用的 NuGet cache。
- 看 summary 裡的 Successful/Failed，不要只看開頭的「Successfully generated」。

## 非 DevKit 但本次環境也踩到：相對路徑 `apply_patch` 沒落在 repo

工具：

```text
apply_patch
```

現象：

用相對路徑新增 `tmp/agent_mcp_scratch.fsx` 時工具回 success，但 shell 與 git 都看不到檔案。改用絕對路徑：

```text
G:/PulseTrade.fs/Libs/FSharp.MCP.DevKit/tmp/agent_mcp_scratch.fsx
```

才正確落在 repo。

影響：

這不是 FSharp DevKit Local 的 bug，但會影響 Codex Agent 在 Windows workspace 裡準備 scratch files。

暫時 workaround：

- 在 Windows repo 內寫檔時，優先使用絕對路徑。
- 寫完立刻用 shell `Test-Path` 或 `git status --short` 驗證。

## 沒有實測的危險工具

```text
kill_all
```

原因：

它會 kill MCP server processes，測了可能直接中斷本任務。建議只在隔離環境或故障復原測試中使用。
