# SA

## 問題現況

- [McpFsiTools.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/McpFsiTools.fs) 的 `ExecuteFSharpCode` 直接 `let! response = client.ExecuteCode(code, timeout)`，直到 FSI 回應才結束。
- [NamedPipeIPC.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Communication/NamedPipeIPC.fs) 的 `PipeServer.ProcessCommand` 收到 `EXEC` 後立刻執行 `fsiService.ExecuteInteraction(...)`。
- [Program.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/Program.fs) 目前只暴露 `/mcp`，沒有 async job 狀態查詢路徑。
- [FSIService.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Core/FSIService.fs) 已有 `FsiResult` 與 `ExecuteInteractionAsync`，適合拿來當 queue 完成後的標準結果型別。

## 根因

1. MCP tool 對 FSI 的呼叫模式是 request-response 同步等待。
2. 目前沒有 job queue 與結果快取，因此無法先回 `asyncId` 再晚點查詢。
3. 沒有單一排程器來保證同一個 FSI session 的順序執行。

## 範圍

### In Scope

- 新增 async code execution tool。
- 新增 FIFO queue 與背景排程器。
- 新增 `<asyncId, FsiResult option>` cache。
- 新增 HTTP GET endpoint 查詢狀態。

### Out of Scope

- 不改動既有同步 tool 的返回格式。
- 不處理跨程序持久化 queue。
- 不新增資料庫或外部訊息佇列。

## 約束

- `FsiResult option` cache 要定義在 [FSIService.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Core/FSIService.fs) 所在模組邊界內可引用。
- 排程必須依 enqueue 順序單執行緒消費。
- HTTP endpoint 最低要求是能判斷 `asyncId` 對應值是否不為 `None`。

## 介面影響

- MCP tools：新增 `ExecuteFSharpCodeAsync(code, ?timeoutSeconds)`。
- DI service：`FsiMcpService` 新增 enqueue / query 能力。
- HTTP：新增 `GET /fsi/async/{asyncId}`。
