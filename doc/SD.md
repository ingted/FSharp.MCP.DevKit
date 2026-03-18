# SD

## 設計概覽

### 1. Cache 型別

- 在 [FSIService.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Core/FSIService.fs) 新增共用型別別名與 DTO：
  - `type AsyncFsiResultCache = ConcurrentDictionary<string, FsiResult option>`
  - `type AsyncFsiStatusDto = { AsyncId: string; Exists: bool; IsCompleted: bool; Result: AsyncFsiResultDto option }`
  - `type AsyncFsiResultDto = { Output: string; Errors: string; IsSuccess: bool; ExecutionTimeMs: float option }`

### 2. Queue 與 Scheduler

- 在 [McpFsiTools.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/McpFsiTools.fs) 的 `FsiMcpService` 內加入：
  - `Channel<AsyncFsiExecutionRequest>`
  - `AsyncFsiResultCache`
  - 單一 background worker `Task`
- enqueue 時序：
  1. 產生 `asyncId`
  2. `cache[asyncId] <- None`
  3. request 寫入 channel
  4. 立即回 `asyncId`
- worker 時序：
  1. 依序讀取 request
  2. 確保 FSI 已啟動
  3. 直接呼叫底層 `FsiService.ExecuteInteractionAsync(...)`
  4. `cache[asyncId] <- Some result`

### 3. 為什麼不直接沿用 PipeClient 做 async queue

- `PipeClient` 只保證單次 request-response，不保證整體 FIFO scheduling。
- `PipeServer` 目前可接受多 connection slot，若 queue 只放在 client 端，仍可能有別的同步請求同時進來。
- 放在 `FsiMcpService` 可以把 async queue 與 HTTP status 查詢收斂到同一個 singleton service。

## 新增資料結構

```fsharp
type AsyncFsiExecutionRequest =
    { AsyncId: string
      Code: string
      Timeout: TimeSpan
      EnqueuedAt: DateTime }
```

## 新增方法

### FsiMcpService

- `member EnqueueExecuteCode(code: string, timeout: TimeSpan) : string`
- `member TryGetAsyncExecution(asyncId: string) : FsiResult option option`
- `member GetAsyncExecutionStatus(asyncId: string) : AsyncFsiStatusDto`

## HTTP Endpoint

- Route: `GET /fsi/async/{asyncId}`
- Response:

```json
{
  "asyncId": "string",
  "exists": true,
  "isCompleted": false,
  "result": null
}
```

- endpoint 一律回 `200 OK`。
- 未知 `asyncId` 以 `exists = false` 表示，而不是 `404`。
- 完成後 `result` 會帶出簡化版結果摘要。

## MCP Tool

- F# 方法：`ExecuteFSharpCodeAsync`
- MCP tool name：`execute_f_sharp_code_async`
- 描述：enqueue F# code execution and return async id immediately，並在 agent-facing description 明示最佳流程：
  1. 呼叫 `execute_f_sharp_code_async` 取得 `asyncId`
  2. 讀取 MCP resource `fsi/async/{asyncId}`
  3. 持續輪詢直到 `isCompleted = true`
- 回傳：`asyncId`

## MCP Resource Template

- 名稱：`fsiAsyncStatus`
- UriTemplate：`fsi/async/{asyncId}`
- 用途：把原本的 HTTP status 查詢包成 MCP resource，讓 agent 不必跳出 MCP transport。
- 回傳 payload 與 HTTP endpoint 對齊：

```json
{
  "asyncId": "string",
  "exists": true,
  "isCompleted": false,
  "result": null
}
```

## 失敗處理

- enqueue 失敗：直接回 exception 訊息。
- worker 執行例外：將 `Some failedResult` 寫回 cache，而不是留在 `None`。
- 查詢未知 `asyncId`：`exists = false`，HTTP 維持 `200 + status payload`。
