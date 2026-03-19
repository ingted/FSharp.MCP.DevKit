# DevLog

## 2026-03-19 10:57:43

- 背景：使用者要求 review `FSharp.MCP.DevKit` 與 `FSharp.MCP.DevKit - Async - 複製`，並在 `FSharp.MCP.DevKit - Async` 做合併開發。
- 動作：
  - 讀取 `E:\dk\AGENTS.md`
  - 確認 `check.fsx` 已補入 workspace
  - 建立 baseline log 與 op_log
  - 針對三個 repo 執行 baseline build
  - 收斂 mixed-runtime 為主幹、async queue 併入遠端 backend 的設計方向
  - 重寫 `doc/SA.md`、`doc/SD.md`、`doc/WBS.md`
  - 建立 `doc/Test.md`、`doc/Policy.md`、`doc/Action.md`
- 結果：
  - 三個 repo 均被 `csharp-sdk` 的 `NU1903` 先卡住 restore
  - target repo 另確認出 `18081/8081` port mismatch、`Timeout = timeout` 型別錯誤、`Core.fsproj` 漏編 `FsiActor.fs`
- 風險：
  - external tracking 尚未提供，`check.fsx` 可能因此失敗
  - target repo 為半合併狀態，需先收斂 backend 再談功能驗證
- 關聯：
  - `doc/SA.md`
  - `doc/SD.md`
  - `doc/WBS.md`
  - `doc/Test.md`
  - `doc/Policy.md`
  - `doc/Action.md`

## 2026-03-19 11:32:30

- 背景：完成 mixed-runtime merge 實作後，需要確認不是只有 compile success，而是 net472 host 與 net9/net10 server 的 sync / async session flow 真正可運作。
- 動作：
  - 將 `FsiActor.fs` 的 compile owner 固定為 `FsiHost`
  - 在 `Messages` 建立 transport-safe remote DTO
  - 在 `McpFsiTools.fs` 導入 `RemoteFsiClient`，移除 server 端本地 FSI backend 依賴
  - 修正 `EnqueueExecuteCode` timeout 型別與 `8081` port 對齊
  - 修正 host / server 解析 `akka*.conf` 的方式，改從輸出目錄讀取
  - 以 `dotnet build FSharp.MCP.DevKit.Async.sln -p:NuGetAudit=false` 重新驗證
  - 啟動 net472 `FsiHost`，再用臨時 .NET smoke app 驗證 sync execute / async queue / polling / package reference / reset
- 結果：
  - solution build success
  - host 成功監聽 `akka.tcp://FsiExecutionSystem@localhost:8081`
  - `let x = 41`、`let y = x + 1`、`EvaluateExpression("y") = 42` 成功
  - `ReferenceNugetPackage("Newtonsoft.Json, 13.0.3")` 成功，後續程式碼可實際使用 package
  - reset 後舊 binding 消失，session reset 行為正確
- 根因判讀：
  - 先前 `Microsoft.Extensions.ObjectPool` 缺失不是 merged backend 壞掉，而是直接用 `dotnet fsi #r` 載入 ASP.NET Core app DLL，沒有正確套用 `runtimeconfig/deps`
  - `akka.conf` / `akka.server.conf` 使用相對路徑則會在 cwd 漂移時失敗，屬實際部署風險，已修正
- 風險：
  - `csharp-sdk` 的 `NU1903` 仍需上游處理
  - `Core` 與 `FsiHost` 仍有數個歷史 warning，未在本輪一併清空
- 關聯：
  - `doc/SA.md`
  - `doc/SD.md`
  - `doc/Test.md`
  - `doc/Action.md`
  - `log/*async-smoke-app*.op_log`
