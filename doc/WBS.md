# WBS

## 分析

- [x] 盤點 `FSIService` / `PipeServer` / `MCP tool` / `HTTP host` 現況
- [x] 定義 async queue 與 cache 的責任邊界

## 設計

- [x] 定義 `asyncId -> FsiResult option` cache
- [x] 定義 FIFO scheduler 與 HTTP status DTO
- [x] 定義 endpoint 路徑與回傳格式

## 開發

- [x] 在 [FSIService.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Core/FSIService.fs) 新增 async cache/status 型別
- [x] 在 [McpFsiTools.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/McpFsiTools.fs) 加入 queue、worker、cache query 與 `ExecuteFSharpCodeAsync`
- [x] 在 [Program.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/Program.fs) 增加 `GET /fsi/async/{asyncId}`

## 驗證

- [x] `dotnet build FSharp.MCP.DevKit.sln`
- [x] 至少驗證一次 enqueue 後可查到 `exists=true`
- [x] 驗證完成後 `isCompleted=true`

## 收尾

- [x] 更新 log 實際結果
- [ ] 執行 `check.fsx`
