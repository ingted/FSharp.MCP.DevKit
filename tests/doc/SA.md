# Test Project SA

## 背景

`tests/FSharp.MCP.DevKit.Tests.fsproj` 目前只有一個 placeholder 測試，尚未覆蓋本輪已落地的：

- shared domain types
- remote DTO contract
- backend abstraction

若不先補測試結構，後續 `WP03+` 的 registry / routing / backend 整合會缺少回歸保護。

## 測試目標

1. 驗證 `WP01` 新增的 shared contract 沒有破壞既有資料流。
2. 驗證 `WP02` 新增的 backend abstraction 能正確表達 backend selection 與 result mapping。
3. 建立後續 `WP03-WP09` 可沿用的測試專案結構。

## 本輪測試範圍

### 範圍內

- `BackendSelector`
- `BackendAdapters`
- `McpActorMessages` 的 route-aware DTO
- `FsiResult` / `FsiExecutionRecord` 契約的基本一致性

### 範圍外

- 真正啟動 net472 host
- 真正啟動 net10 out-of-proc host
- Akka remoting integration
- MCP HTTP endpoint / resource integration test

## 主要風險

1. F# compile order 容易讓測試專案新增檔案後編譯失敗。
2. `Server` 專案目前不是 tests 專案的 project reference，因此若要測 `BackendSelector` / `BackendAdapters`，需要補上對 `Server` 的參考。
3. transport DTO 是跨專案共用契約，欄位名或 optional 行為若改動，容易造成 build 過但 runtime 相容性受損。

## 驗收標準

1. tests 專案不再只有 placeholder。
2. 至少新增三組單元測試：
   - `BackendSelectorTests`
   - `BackendAdaptersTests`
   - `RemoteMessageContractTests`
3. `dotnet test tests/FSharp.MCP.DevKit.Tests.fsproj -f net10.0` 通過。
4. 測試名稱能直接對應 `WP01/T01-T04` 與 `WP02/T05-T07`。

## 下一步

依本 SA 撰寫測試專案 SD，明確定義測試檔、test double 與案例矩陣，再進入實作。
