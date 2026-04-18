# Agent 使用經驗談

測試日期：2026-04-18
測試環境：Windows / PowerShell / `default-agent` / `default-host` / `default-session`

這份筆記是以「剛出新手村的 Agent」角度，實際摸過 FSharp DevKit Local MCP tools 後整理的上手指南。重點不是列 API，而是說明哪些工具適合先用、怎麼串、哪些行為需要先有心理模型。

## 先確認 FSI 活著

第一步永遠先跑：

```text
check_fsi_server_status
```

成功時會回 `FSI server is running and accessible`。如果只是想讓 default session 回到乾淨狀態，可以用：

```text
restart_fsi_session
reset_fsi_session
```

我的測試中 `restart_fsi_session`、`reset_fsi_session` 都可用。`restart` 會重建 default host/session，`reset` 則清掉目前 session 狀態。

## 最小可用工作流

短程互動直接用：

```text
execute_f_sharp_code
evaluate_f_sharp_expression
execute_f_sharp_code_detailed
get_fsi_state
```

實測範例：

```fsharp
let newbieValue = 21 * 2
newbieValue
```

`execute_f_sharp_code` 回傳 FSI stdout，例如 `val newbieValue: int = 42`。接著 `evaluate_f_sharp_expression` 可以讀到同 session 裡的 binding，例如 `newbieValue + 1` 回 `43`。

長一點或不想阻塞的工作用 async：

```text
execute_f_sharp_code_async
get_async_status
```

`execute_f_sharp_code_async` 先回 asyncId；要輪詢 `get_async_status`，完成後 JSON 裡會有 `resultId`。這個 `resultId` 後面可接 result tools。

## Routed Session 比 Default Session 更適合 Agent

如果工作會跨多步，建議建立自己的 session，不要一直污染 `default-session`。

可用路徑：

```text
create_fsi_session(agentId = "default-agent", hostId = "default-host", sessionId = "your-session")
execute_f_sharp_code_routed
evaluate_f_sharp_expression_routed
execute_f_sharp_code_async_routed
get_fsi_state_routed
reset_fsi_session_routed
```

我實測 `create_fsi_session` 在 `default-host` 下可建立 `newbie-routed-session`。routed code、routed eval、routed async 都正常。失敗後請用 `reset_fsi_session_routed` 復原。

注意：`ensure_fsi_route` 在本次環境中回 generic error，建議先用 `list_fsi_hosts` 找現有 host，再用 `create_fsi_session` 建 session。

## Result Store 是 Agent 的黑盒紀錄本

每次 routed/async/scheduled execution 都會留下 result。常用工具：

```text
list_fsi_results
list_execution_store_records
list_fsi_results_by_session_id
get_fsi_result
get_execution_store_record
get_execution_fabric_record
query_fsi_results
compare_fsi_results
```

實務建議：

- 手上有 `resultId` 時，用 `get_fsi_result` 看原始 execution record。
- 要跨系統或看 output events，用 `get_execution_fabric_record`。
- 要批量篩選成功/失敗，用 `query_fsi_results kind=filter queryText=isSuccess`。
- 要把查詢結果再變成 result，用 `materialization=syntheticResult`，會得到新的 `producedResultIds`。

`compare_fsi_results` 可以快速比兩個結果的 stdout/value 差異，很適合做 smoke check。

## `#I`、`#r`、NuGet、`#load`

常用工具：

```text
add_search_path
add_search_path_routed
reference_assembly
reference_assembly_routed
reference_nu_get_package
load_f_sharp_script
```

實測成功案例：

```text
add_search_path G:\PulseTrade.fs\Libs\FSharp.MCP.DevKit\tmp
reference_assembly System.Text.Json
reference_nu_get_package "Newtonsoft.Json, 13.0.3"
load_f_sharp_script G:\PulseTrade.fs\Libs\FSharp.MCP.DevKit\tmp\agent_mcp_scratch.fsx
```

`reference_nu_get_package` 成功後可以直接：

```fsharp
open Newtonsoft.Json
JsonConvert.SerializeObject([| 1; 2; 3 |])
```

NuGet cache 位置在本次環境是：

```text
C:\Windows\system32\config\systemprofile\.nuget\packages
```

所以不要假設它吃的是互動使用者的 NuGet cache。

## 靜態分析怎麼用

目前最穩的是：

```text
get_all_symbols
get_symbol_at_position
get_symbol_signature_at_position
what_is_at_position
```

`get_all_symbols` 對小型 F# source 很快，會列出 function/value/type/record 與 signature。position-based tools 也能正確辨識，例如 `add`、`describe`、`Person`。

已修復後可用：

```text
analyze_code_structure
parse_and_check_f_sharp_code
parse_source_to_ast
```

2026-04-18 已改走 static FSharpChecker / symbol detection 路徑，不再依賴 FSI interactive session 的 `PARSE`。建議用法：

- `parse_and_check_f_sharp_code`：快速檢查小型 source，會回 static parse/check summary 與 diagnostics。
- `parse_source_to_ast`：目前輸出是 static AST summary + symbol summary，不是完整 AST DTO。
- `analyze_code_structure`：分析 `.fsx/.fs/.fsi` 檔案，回 line/character count、diagnostics、symbol summary。

若需要特定位置的型別或簽章，仍優先使用 `get_symbol_at_position`、`get_symbol_signature_at_position`、`what_is_at_position`。

## 檔案工具的安全用法

讀取與搜尋可用：

```text
count_lines
get_lines
search_in_file
```

以下改檔工具實測可用：

```text
replace_text_range
search_and_replace
move_code_block
delete_lines
format_file
```

建議流程：

1. 先 `count_lines`。
2. 再 `get_lines` 看精準範圍。
3. 小步使用 `replace_text_range` 或 `search_and_replace`。
4. 最後 `format_file`。
5. 再 `get_lines` 驗證。

`insert_code` 與 `preview_code_injection` 已於 2026-04-18 修復。建議用法：

- `preview_code_injection` 先看結果，不會寫檔。
- `insert_code` 僅對已存在 F# 檔案運作，缺檔會直接失敗。
- 寫入前仍建議用 `get_lines` 驗證目標位置；若要建立新檔，改用明確的新檔建立流程，不要靠 `insert_code` 隱含建立。

## 排程工具的基本節奏

立即排程：

```text
schedule_f_sharp_code_routed
process_next_due_schedule_445026308785
```

批次處理：

```text
process_due_scheduled_fsi_eccbe79e35be
```

管理排程：

```text
list_scheduled_fsi_executions
cancel_scheduled_fsi_execution
requeue_failed_scheduled_fsi_execution
requeue_failed_scheduled__eba3171d3ee7
```

實測發現：語法錯誤的 scheduled execution 會讓該 session 進入 `Faulted`。後續 execution 會被擋，錯誤會提示：

```text
Call reset_fsi_session_routed or create_fsi_session to recover.
```

所以排程跑失敗後，第一件事是 reset 或換 session。

## Output、Subscribe、Archive

常用工具：

```text
get_session_output_events
subscribe_session_output
list_session_output_subscribers
unsubscribe_session_output
seal_session_output
get_session_output_archive
get_archived_session_output_events
list_session_output_archives
unregister_fsi_session
```

`get_session_output_events` 很適合追 stdout/stderr，會有 sequenceNo。`subscribe_session_output` 會登記 subscriber，但目前在 MCP 呼叫情境比較像 control-plane metadata，不是即時串流 UI。

`unregister_fsi_session` 實測正常：會移除 live session，並把 output events 封存，之後還能用 `get_archived_session_output_events` 與 `list_fsi_results_by_session_id` 追資料。

`seal_session_output` 的行為要小心，本次看到 archive event count 與 live events 不一致，詳見踩雷文件。

## Documentation Tools

可用工具：

```text
list_cached_packages
show_package_info
set_documentation_output_directory
show_documentation_config
generate_package_documentation
generate_project_documentation
search_documentation
```

成功案例：

```text
reference_nu_get_package "Newtonsoft.Json, 13.0.3"
generate_package_documentation Newtonsoft.Json
search_documentation JsonConvert
```

`generate_project_documentation` 是依專案參考套件去本機 NuGet cache 找文件。如果套件不在 cache，會列為 failed，不會自動 restore。我的測試中 Core 專案 5 個 package 都因 cache 缺失而 failed，這不是工具 crash，但新 Agent 要先知道它不會自動補齊套件。

## Browser Inventory 與 Browser-Aware Execution

可用工具：

```text
register_browser_inventory
list_browser_inventory
get_browser_inventory
remove_browser_inventory
execute_browser_f_sharp_code_routed
schedule_browser_f_sharp_code_routed
```

這組名字容易誤會。它不是直接控制瀏覽器，而是把 execution 標記上 browser metadata，實際執行仍在 companion FSI session。`get_execution_fabric_record` 會看到：

```text
browser.id
browser.tabId
browser.executionPlane
schedule.target.browserId
schedule.target.tabId
```

適合拿來串 SharpBrowser / companion session 的執行紀錄。

## WinAgent Import

可用工具：

```text
import_winagent_execution_envelope
import_winagent_execution_f45c3ac1645c
```

單筆 JSON envelope import 成功後，會建立 result record 與 output event。JSONL bulk import 會略過 invalid lines，summary 會回：

```text
importedCount
resultIds
skippedCount
errors
```

這對整合外部 WinAgent 執行紀錄很有用。

## 不建議新手直接碰的工具

```text
kill_all
create_fsi_host
```

`kill_all` 會殺 MCP server processes，除非你要做災難恢復測試，否則不要在一般任務中碰。

`create_fsi_host` 不是一般 process launcher，它期待 netfx/net10 ProcSupervisor host。用 `dotnet --version` 這種普通 process 測試只會 generic error。要用前先看部署文件與 hostKind/procnode 參數。

## 新手村 SOP

1. `check_fsi_server_status`
2. `list_fsi_hosts default-agent`
3. `create_fsi_session default-agent default-host <your-session>`
4. 用 routed tools 執行工作。
5. 每次 async/schedule 都保存 `resultId`。
6. 查問題先看 `get_session_output_events`，再看 `get_fsi_result`。
7. 遇到 syntax error 或 session faulted，先 `reset_fsi_session_routed`。
8. 改檔先用 `get_lines/count_lines/search_in_file`，避免用 `insert_code`。
9. 長任務用 `execute_f_sharp_code_async_routed` 或 scheduler。
10. 任務結束要可追溯時，用 `unregister_fsi_session` 或 result store 查詢，不要只看聊天紀錄。
