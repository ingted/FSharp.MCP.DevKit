# SD

## 2026-03-28 get_async_status

### Design Summary

新增一個單純包裝 `FsiMcpService.GetAsyncExecutionStatus(asyncId)` 的 MCP tool：

- tool name: `get_async_status`
- input: `asyncId: string`
- output: `AsyncFsiStatusDto` 的 JSON 字串

### Why A Tool Instead Of Another Routed API

async job 本來就是以 `asyncId` 為主鍵，不依賴 caller 再提供 route。因此不需要再做：

- `get_async_status_routed`
- `get_async_status_for_host`

單一工具即可同時服務：

- `execute_f_sharp_code_async`
- `execute_f_sharp_code_async_routed`

### Placement

放在 `FSharpInteractiveTools`，因為：

1. async status 查詢不需要 route 參數
2. 這是給 agent 的通用工具，不是 control-plane 專屬流程

### Behavior

1. 直接呼叫 `fsiService.GetAsyncExecutionStatus(asyncId)`
2. 將 `AsyncFsiStatusDto` 用 `FSharpJson.serialize` 回傳
3. 若 `asyncId` 不存在，仍回 `Exists=false` 的 DTO，而不是丟 exception

### Test Plan

1. `McpSurfaceTests`
   - 驗證 `get_async_status` 與 resource 讀到相同狀態
2. `McpClientAvailabilityTests`
   - 驗證 tool surface 能 discover `get_async_status`
3. `McpClientSmokeTests`
   - async smoke 改走 `get_async_status` 輪詢，證明 client 不依賴 `resources/read`
