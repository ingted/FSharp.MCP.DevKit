# Test

## 測試目標

- 驗證 mixed-runtime backend 收斂後，sync / async FSI tools 共用同一個遠端 session。
- 驗證 async queue、status polling、Reset / Restart 行為正確。
- 驗證 merge 後沒有再出現 target framework / actor compile path 漏接。

## 前置條件

- `FSharp.MCP.DevKit - Async` 已完成本輪 code merge。
- local build 以 workaround 排除 `csharp-sdk` 上游 `NU1903` restore gate，僅用於顯示本 repo 真正的 compile 結果。
- `FSharp.MCP.DevKit.FsiHost` 可單獨啟動。

## 測試案例

| ID | 類型 | 案例 | 步驟 | 預期 |
|---|---|---|---|---|
| TC01 | Build | Solution compile | `dotnet build FSharp.MCP.DevKit.Async.sln <workaround>` | 無本 repo 造成的 compile error |
| TC02 | Smoke | Host 啟動 | 啟動 `FSharp.MCP.DevKit.FsiHost` | Akka host 監聽設定 port |
| TC03 | Smoke | Sync execute | 呼叫 `ExecuteFSharpCode` 執行 `let x = 1` | 成功、無 transport error |
| TC04 | Session | Incremental snippet | 先 `let x = 41`，再 `x + 1` | 第二次可讀到同一 session 狀態 |
| TC05 | Async | Async execute enqueue | 呼叫 `execute_f_sharp_code_async` | 立即回傳 `asyncId` |
| TC06 | Async | Async polling | 讀取 `fsi/async/{asyncId}` 或 `/fsi/async/{asyncId}` | `exists=true`，完成後 `isCompleted=true` |
| TC07 | Session | Async session continuity | sync 設定 state，async 讀取該 state | async worker 看到同一遠端 session 狀態 |
| TC08 | Session | Reference package then execute | `ReferenceNugetPackage` 後執行使用該 package 的 code | 使用同一 session 成功 |
| TC09 | Session | Reset | 呼叫 `Reset` 後再執行先前 binding | 舊 binding 消失 |
| TC10 | Session | Restart | 呼叫 `Restart` 後再查 `GetState` | session 重新建立 |
| TC11 | Error | Akka port mismatch regression | host / server 以既定 port 啟動 | 不再出現 `18081` / `8081` mismatch |
| TC12 | Regression | ParseAndCheck | 呼叫 parse/check tool | 有正確 diagnostics，無 backend crash |
| TC13 | Security | Audit-enabled solution build | `dotnet build FSharp.MCP.DevKit.Async.sln` | 不再出現 `Akka.Remote` `NU1904` |
| TC14 | Security | Project vulnerability audit | 對 `FsiHost` / `Server` 執行 `dotnet list package --vulnerable --include-transitive --no-restore` | 兩個專案皆無 vulnerable packages |

## 本輪最低驗收

- TC01
- TC03
- TC04
- TC05
- TC06
- TC08
- TC09
- TC11

## 本輪執行結果

| ID | 結果 | 證據 |
|---|---|---|
| TC01 | pass | `log/*async-smoke-app.op_log` 內含 solution build success |
| TC02 | pass | host stdout 顯示 `akka.tcp://FsiExecutionSystem@localhost:8081` |
| TC03 | pass | `let x = 41` 成功 |
| TC04 | pass | 後續 `y = x + 1`、`EvaluateExpression("y") = 42` |
| TC05 | pass | 成功取得 `asyncId` |
| TC06 | pass | polling 最終 `isCompleted=true` |
| TC08 | pass | `ReferenceNugetPackage("Newtonsoft.Json, 13.0.3")` 後成功序列化 JSON |
| TC09 | pass | reset 後 `y` 不再可見 |
| TC11 | pass | host / server 全程使用 `8081` |
| TC13 | pass | `log/20260319130030.build-after-akka-update.op_log` 無 `NU1904` |
| TC14 | pass | `log/20260319130033.fsihost-vulnerable.op_log`、`log/20260319130034.server-vulnerable.op_log` |

## 測試方法補充

- 為避免 `dotnet fsi #r webapp.dll` 對 ASP.NET Core app 的 shared framework 載入偏差，本輪 smoke 採「啟動真實 net472 host + 臨時 .NET app ProjectReference server project」方式驗證。
- 這個 smoke harness 是驗證工具，不屬於 target repo 交付物。

## 風險註記

- 若 `csharp-sdk` 的 `NU1903` 仍阻擋 restore，需用 local workaround 驗證 compile，並在 DevLog 註記這是外部依賴風險，不是本 repo merge 完成的最終解。
