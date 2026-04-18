# Agent 排雷建議計畫

撰寫日期：2026-04-18
目標讀者：後續負責修復 FSharp DevKit Local MCP tools 的 Agent

本計畫聚焦兩個優先排雷項目：

1. `insert_code` / `preview_code_injection` 的檔案插入安全性問題。
2. `parse_and_check_f_sharp_code` / `parse_source_to_ast` / `analyze_code_structure` 的 timeout 問題。

這兩項會直接影響新進 Agent 是否能放心使用 DevKit 做程式修改與分析。第一項有資料毀損風險，應優先處理。

## P0：修復 `insert_code` 可能覆寫整檔

### 修復狀態

2026-04-18 已完成。

- `preview_code_injection` 與 `insert_code` 已共用 `planCodeInsertion`。
- `insert_code` 不再對不存在檔案建立/覆寫內容，缺檔會直接回 `File not found`。
- 寫入前加入原文保留 sanity guard。
- 寫入流程改為 UTF-8 temp file + Windows `File.Replace`，降低半寫入風險。
- 已補 `McpSurfaceTests` regression tests，覆蓋 preview、insert、missing file。

### 實測發現

測試檔案：

```text
G:\PulseTrade.fs\Libs\FSharp.MCP.DevKit\tmp\agent_mcp_scratch.fsx
```

該檔經 `count_lines` 確認有 14 行，`get_lines` 也能正常讀出內容。但同一路徑呼叫：

```text
preview_code_injection(insertAtLine=14 或 15)
insert_code(insertAtLine=14)
```

回報：

```text
Error: Invalid line number <n>. File has 0 lines.
```

接著呼叫：

```text
insert_code(insertAtLine=1, newCode="// inserted header", shouldFormat=false, shouldValidate=false)
```

工具回成功，但實際檔案只剩：

```fsharp
// inserted header
```

結論：`insert_code` 在某些 MCP 實際呼叫情境下會把 existing content 當成空檔處理，並把原檔覆寫成 `newCode`。這是資料毀損級問題。

### 初步定位

主要程式碼位置：

```text
src/FSharp.MCP.DevKit.Server/McpFsiTools.fs
```

相關區段：

- `splitLinesPreservingLineEndings` / `joinLinesWithConsistentEndings`：約第 164-172 行。
- `PreviewCodeInjection`：約第 2227-2307 行。
- `InsertCode`：約第 2805-3020 行。

`PreviewCodeInjection` 與 `InsertCode` 都有獨立一份讀檔、切行、合併邏輯。實測兩者都出現 `File has 0 lines`，所以問題可能在共用 helper、路徑/存在判斷、MCP surface 參數綁定、或「某些工具讀檔語意不同步」。

目前程式看起來的高風險點：

- `InsertCode` 若 `File.Exists(filePath)` 為 false，會把 `existingCode` 設為空字串，後續仍可在 line 1 寫入，等同建立/覆寫檔案。
- `writeFileSafely` 會先 backup、寫 temp，再 delete 原檔與 move temp；若上游 `finalCode` 已錯，安全寫入也會安全地寫錯。
- `preview` 和 `insert` 各自實作合併邏輯，未保證 preview 結果與 insert 實際寫入完全一致。
- `splitLinesPreservingLineEndings` 名稱說 preserving，但實作是 `String.Split`，實際不保留 line endings；雖然這不一定是本次覆寫根因，但容易造成 off-by-one 或 newline 語意漂移。

### 建議修正方式

1. 抽出單一純函式做 code insertion planning。

建議新增內部函式，例如：

```fsharp
type InsertPlan =
    { OriginalText: string
      NewText: string
      OriginalLineCount: int
      InsertedLineCount: int
      InsertAtLine: int
      ContextWarning: string option }

let planCodeInsertion filePath existingText newCode insertAtLine insertAtColumn : Result<InsertPlan, string>
```

`PreviewCodeInjection` 與 `InsertCode` 必須呼叫同一個 `planCodeInsertion`，避免 preview/actual 邏輯分叉。

2. 對既有檔案採「不可默默當空檔」策略。

若 `File.Exists(filePath)` 為 false，應明確分兩種 API/模式：

- 插入既有檔案：檔案不存在就失敗。
- 建立新檔：只能在明確 create mode 下允許。

目前 `insert_code` 的描述是「target F# file」，建議預設只允許既有檔案。若要支援新檔，另加明確參數或新工具。

3. 寫入前加入 sanity guard。

若原檔存在且 `OriginalText.Length > 0`，但 `NewText` 不包含原文任何可辨識片段，或 `NewText.Length` 小於原文一個危險比例，應拒絕寫入並回傳錯誤。例如：

```text
Refusing to write: insertion output is shorter than original and does not preserve original content.
```

這個 guard 不能取代正確合併，但可以避免 P0 資料毀損再次發生。

4. 使用一致 encoding 與 atomic replace。

目前部分工具 `WriteAllText` 沒指定 encoding，部分指定 UTF8。建議 insertion path 用：

- 讀取時偵測 BOM 或至少保留 UTF-8。
- 寫入 temp 時指定 encoding。
- 優先用 `File.Replace(tempPath, filePath, backupPath)`，Windows 上比 delete/move 更接近 atomic replace。

5. 修正 line split 語意。

目前 helper 不是真的 preserving。可改成：

- 若只需要 line-based insertion，使用明確的 logical lines，不要稱 preserving。
- 若要保留 newline style，先偵測原檔主要 newline (`\r\n`, `\n`, `\r`)，join 時沿用。
- 空檔 line count 應定義清楚：空字串是 0 行，單行無 newline 是 1 行。

### 建議測試

新增 MCP surface 或 tool-level regression tests，優先放在：

```text
tests/McpSurfaceTests.fs
tests/McpExecutionToolsTests.fs
tests/McpClientE2ETests.fs
```

若目前沒有適合檔案編輯工具的 test file，可新增 `McpCodeEditingToolsTests.fs`。

必要案例：

- 已存在 3 行 `.fsx`，在 line 1 插入一行，原 3 行仍保留。
- 已存在 3 行 `.fsx`，在 line 4 append，一共 4 行。
- 已存在 3 行 `.fsx`，在 line 2 插入多行，順序正確。
- `preview_code_injection` 的輸出內容與 `insert_code` 實際寫入內容一致。
- 對不存在檔案呼叫 `insert_code` 應失敗，除非明確支援 create mode。
- 插入後 `count_lines` / `get_lines` 能讀到正確行數與內容。
- Windows path `G:\...\file.fsx` 與 slash path `G:/.../file.fsx` 都要覆蓋。

### 驗收標準

- `insert_code` 不再覆寫掉原檔內容。
- `preview_code_injection` 不再對存在檔案回報 `File has 0 lines`。
- preview 與 actual insert 使用同一個 planning function。
- 測試能重現舊問題並在修正後通過。

## P1：修復 parse/check/analyze timeout

### 修復狀態

2026-04-18 已完成第一階段修復。

- `parse_and_check_f_sharp_code` 改走 static FSharpChecker，不再呼叫 FSI interactive `PARSE`。
- `parse_source_to_ast` 改走 static parse/check + symbol summary。
- `analyze_code_structure` 改走 static parse/check + symbol summary。
- 已補 `McpSurfaceTests` regression tests，覆蓋合法小型 source、非法 diagnostics、source AST summary、file structure summary。
- 後續若要更精準 AST，仍可再拆出 dedicated AST DTO；目前輸出定位是 static parse/check summary + symbol summary。

### 實測發現

以下工具在 14 行左右的小型 F# source/file 上 timeout：

```text
parse_and_check_f_sharp_code
parse_source_to_ast
analyze_code_structure
```

觀察到的回傳：

```text
Error parsing code: Timeout after 30.00 seconds
Parse failed: Timeout after 30.00 seconds
Error analyzing file: Timeout after 30.00 seconds
```

同一份 source 使用以下工具正常：

```text
get_all_symbols
get_symbol_at_position
get_symbol_signature_at_position
what_is_at_position
```

因此 FSharp.Compiler.Service 本身不是完全不可用，問題更可能在 parse/check tools 目前走的執行路徑。

### 初步定位

主要程式碼位置：

```text
src/FSharp.MCP.DevKit.Server/McpFsiTools.fs
src/FSharp.MCP.DevKit.Core/FSIService.fs
```

相關區段：

- `ParseAndCheckFSharpCode`：`McpFsiTools.fs` 約第 1987-2022 行。
- `ParseSourceToAST`：`McpFsiTools.fs` 約第 2094-2143 行。
- `AnalyzeCodeStructure`：`McpFsiTools.fs` 約第 2146-2223 行。
- `FSIService.ParseAndCheck`：`FSIService.fs` 約第 756-812 行。
- 穩定可參考路徑：`GetAllSymbols` 使用 `SmartSymbolDetection.createSymbolDetectionService()`，約第 3060 行起。

目前三個 timeout 工具都依賴：

```fsharp
client.ParseAndCheck(...)
```

而 in-proc parse/check 的核心在 `FSIService.ParseAndCheck`，它使用：

```fsharp
use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
let task = Task.Run(fun () -> session.ParseAndCheckInteraction(code))
if task.Wait(30000) then ...
else cts.Cancel()
```

高風險點：

- `cts.Token` 沒有傳進 `Task.Run`，`cts.Cancel()` 對已經卡住的 compiler work 沒有實質取消效果。
- `task.Wait(30000)` timeout 後，背景 task 可能仍在跑，後續 parse/check 可能堆積或卡住同一 FSI session。
- `ParseAndCheckInteraction` 是 FSI session 互動式檢查，對「任意 source」或完整 script analysis 不一定是最穩定的 API。
- `ParseSourceToAST` 名稱說 AST，但目前只回 diagnostic summary，沒有真正 AST 或結構資訊。
- `AnalyzeCodeStructure` 也是先 parse/check，再做簡單 line/diagnostic summary；它跟 symbol tools 的穩定服務沒有整合。

### 建議修正方式

1. 把「靜態 source/file 分析」從 FSI session parse/check 解耦。

對 `parse_source_to_ast` 與 `analyze_code_structure`，建議改走 Analysis project 內已有的 FSharpChecker-based service，而不是 `client.ParseAndCheck`。

可參考：

```text
src/FSharp.MCP.DevKit.Analysis/SmartSymbolDetectionService.fs
src/FSharp.MCP.DevKit.Analysis/ImprovedSymbolDetection.fs
```

短期可先實作：

- `parse_source_to_ast`：回傳 parse/check diagnostic summary，底層使用 `FSharpChecker.ParseAndCheckFileInProject` 或 parse-only API。
- `analyze_code_structure`：回傳 line count、module/type/function/value summary，可直接利用 `SmartSymbolDetection.GetAllSymbols` 聚合。

2. 修正 `FSIService.ParseAndCheck` 的 timeout/cancellation。

至少要做到：

- `Task.Run` 傳入 cancellation token。
- timeout 後不要留下不可觀測的 background task。
- 若 compiler API 不支援中止，timeout 後標記該 parse worker/session 不可重用，或使用短生命週期 worker。

但更建議：不要用 FSI session 執行靜態 parse/check。FSI session 應保留給互動式 execution/evaluation。

3. 將 timeoutSeconds 往下傳到底層。

`ParseAndCheckFSharpCode` 有 `timeoutSeconds` 參數，但 `FSIService.ParseAndCheck` 內部固定 30 秒。應統一路徑：

```text
MCP tool timeoutSeconds -> client timeout -> service timeout -> compiler operation timeout
```

若底層無法保證取消，回傳訊息需明確說明是 hard timeout 還是 client wait timeout。

4. 避免 timeout 後污染 session。

新增 guard：parse/check timeout 後，下一次同工具呼叫不應因前一次背景 task 仍跑而卡住。可採：

- parse/check 使用獨立 checker instance。
- 或 parse/check 使用專用 serial queue，timeout 後重建 checker。
- 或在 in-proc FSI parse/check timeout 後自動 reset/restart parse backend。

### 建議測試

新增 regression tests，建議至少包含：

- `parse_and_check_f_sharp_code` 對 5-20 行合法 source 在 5 秒內成功。
- `parse_and_check_f_sharp_code` 對非法 source 在 5 秒內回 diagnostics，不 timeout。
- `parse_source_to_ast` 對合法 source 在 5 秒內成功，且回傳內容不是空 summary。
- `analyze_code_structure` 對小 `.fsx` 在 5 秒內成功，回傳 line count 與至少一個 function/value/type summary。
- 連續呼叫 3 次 parse/check，不因第一次 timeout 或錯誤污染後續呼叫。
- timeout 測試：人工注入 slow checker 或 mock client，確認 timeout 後沒有未觀測背景 work 影響下一次測試。

測試位置建議：

```text
tests/SmartSymbolDetectionServiceTests.fs
tests/McpSurfaceTests.fs
tests/McpClientSmokeTests.fs
```

若要直接覆蓋 MCP tool surface，新增 `McpAnalysisToolsTests.fs` 會更清楚。

### 驗收標準

- 小型 source/file 不再 30 秒 timeout。
- parse/check 的 timeoutSeconds 能被遵守。
- timeout 後後續 parse/check 不被污染。
- `parse_source_to_ast` 的名稱與輸出語意一致；若暫時不回 AST，應改名或在輸出中明確說明是 parse diagnostics。
- `analyze_code_structure` 可用於新 Agent 的 codebase 快速掃描。

## 建議修復順序

2026-04-18 執行狀態：

- 1 到 4 已完成。
- 5 已部分完成：`Agent使用經驗談.md` 已改為可用建議；P2/P3 尚保留為踩雷項目。

1. 先補 regression tests，重現 `insert_code` 覆寫與 `preview_code_injection` 0 lines。
2. 修 `insert_code` / `preview_code_injection`，並加寫入前 sanity guard。
3. 補 parse/check/analyze timeout regression tests。
4. 把 static analysis tools 改走 FSharpChecker/SmartSymbolDetection，不再依賴 FSI interactive parse/check。
5. 最後更新 `Agent使用經驗談.md`，移除對這些工具的暫禁用提醒。

## 修復完成後建議手動驗證

用 MCP tool 實際跑一次，不只跑單元測試：

```text
check_fsi_server_status
count_lines scratch.fsx
preview_code_injection scratch.fsx line 2
insert_code scratch.fsx line 2
get_lines scratch.fsx
parse_and_check_f_sharp_code small valid source
parse_source_to_ast small valid source
analyze_code_structure scratch.fsx
```

驗證重點：

- 插入前後原文保留。
- preview 與實際寫入一致。
- parse/analyze 在小檔案上快速完成。
- 錯誤訊息足以讓 Agent 判斷下一步，不再只有 timeout 或 generic failure。
