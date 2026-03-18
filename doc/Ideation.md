# Async FSI Tool Calling 發想

## 需求摘要

- 現況 `execute_f_sharp_code` 會同步等待 FSI 執行完成，造成 tool calling 對 agent 是 blocking。
- 需要新增 `execute_f_sharp_code_async`，收到請求後立刻回 `asyncId`。
- 原本同步要直接吐回 agent 的執行結果，先放到 `<asyncId, FsiResult option>` cache。
- 執行順序必須依收到順序 FIFO，不可平行搶占同一個 FSI session。
- HTTP 需要有 GET endpoint 能檢查某個 `asyncId` 是否已經從 `None` 變成 `Some`.

## 主要想法

1. queue 不放在 pipe client，而放在 `FsiMcpService`。
   - 原因：`FsiMcpService` 同時位於 MCP tool 與 HTTP route 的共用 service 邊界。
   - 這樣可直接共用同一份 cache，不需要再跨層同步狀態。

2. queue 使用單一 consumer。
   - 只要 consumer 永遠一次處理一個 request，就能保證同一個 FSI session 的執行順序。
   - 比起多個 pipe connection slot，更符合「依序排程」需求。

3. cache 以 `ConcurrentDictionary<string, FsiResult option>` 保存。
   - enqueue 時先放 `None`。
   - 執行完成或失敗時改寫成 `Some result`。
   - HTTP GET 只要檢查是否為 `Some` 即可回報完成狀態。

4. async tool 與同步 tool 並存。
   - `ExecuteFSharpCode` 保留同步行為，避免破壞現有 client。
   - `ExecuteFSharpCodeAsync` 提供新路徑。

5. HTTP endpoint 回傳狀態為主，結果摘要為輔。
   - 最低限度要有 `asyncId`、`exists`、`isCompleted`。
   - 若已完成，可附帶 `isSuccess`、`output`、`errors`，方便人工除錯。

## 風險

- FSI 本身若執行長時間或卡住，FIFO queue 後續請求也會跟著排隊。
- 現有 `FsiResult.Value` 與 `FSharpDiagnostic[]` 不適合直接對外 JSON 化，HTTP DTO 需要降維。
- `examples/ExampleTool/ExampleTool.fsproj` 是壞檔，不能依賴 workspace 級自動分析。
