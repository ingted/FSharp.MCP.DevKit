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

## 2026-03-19 13:00:34

- 背景：使用者要求更新上游 dependency，目標是移除目前 build audit 的已知高風險套件。
- 動作：
  - 重新檢查 `csharp-sdk` 與 target repo 的 vulnerability 狀態
  - 確認 `csharp-sdk` 當前 audit 已無 vulnerable packages，但其 repo 有既存未提交變更：`Directory.Packages.props`、`global.json`
  - 重新以不帶 `NuGetAudit=false` 的方式 build target repo，定位真正的 critical dependency 為 `Akka.Remote 1.5.30`
  - 將 `FSharp.MCP.DevKit.FsiHost.fsproj`、`FSharp.MCP.DevKit.Server.fsproj` 的 `Akka` / `Akka.Remote` 升到 `1.5.62`
  - 重新執行 `dotnet restore`、`dotnet build`、project-level vulnerability audit
- 結果：
  - target repo build 不再出現 `NU1904`
  - `FSharp.MCP.DevKit.FsiHost` 與 `FSharp.MCP.DevKit.Server` 的 project-level vulnerability audit 均回報無 vulnerable packages
  - 仍保留既有非安全性 warning：`NU1701`、`NU1510`、`MSB3245`、`MSB3243`
- 根因判讀：
  - 先前真正需要處理的是 target repo 的 `Akka.Remote 1.5.30`，不是 `csharp-sdk` 的既有 central package
  - solution-level `dotnet list package --vulnerable` 在本 repo 會受 NuGet source mapping / restore 路徑差異影響，因此本輪以 project-level audit 作為安全性驗證證據
- 風險：
  - `csharp-sdk` 仍有使用者或其他流程留下的未提交變更，本輪未主動覆蓋
  - `NuGet.* 7.3.0` 的 `NU1701` 與 net472 reference resolution warning 仍待獨立整理
- 關聯：
  - `doc/SA.md`
  - `doc/SD.md`
  - `doc/WBS.md`
  - `doc/Test.md`
  - `doc/Action.md`
  - `log/*async-smoke-app*.op_log`

## 2026-03-19 14:05:23

- 背景：使用者要求兩件事同步完成：
  - 把 `FsiActor.fs` 從「放在 Core 路徑、由 FsiHost link compile」改成直接移入 `FsiHost`
  - 提供可直接部署到遠端 Windows 主機的腳本，能自動複製 artifact、註冊 `fsihost` 與 `fsharp-devkit` 兩個服務
- 動作：
  - 將 `src/FSharp.MCP.DevKit.Core/FsiActor.fs` 實體移到 `src/FSharp.MCP.DevKit.FsiHost/FsiActor.fs`
  - 更新 `FSharp.MCP.DevKit.FsiHost.fsproj`，改為正常 `Compile Include="FsiActor.fs"`
  - 更新 `FsiHost/Program.fs`，加入 `--service-name` 解析與 Windows service mode
  - 更新 `Server/Program.fs`，加入 `UseWindowsService` service name 設定與 `/healthz` 回傳 `serviceName`
  - 建立 `scripts/deploy-remote-services.ps1`
  - 盤點 `scripts/` 既有腳本，將 placeholder 改為 fail-fast，並新增 `scripts/README.md`
  - 補齊 `doc/Deployment.md`、`doc/Runbook.md`
- 驗證：
  - 初次平行驗證時，同時執行 solution build 與兩個 publish，導致本機出現 `Stack overflow`、`MSB6006`、分頁檔不足；此為驗證方式造成的資源競爭，不是部署腳本本身的邏輯錯誤
  - 關閉殘留 `dotnet` process 與 build server 後，改為單工序列驗證：
    - `dotnet build FSharp.MCP.DevKit.Async.sln -m:1`
    - `dotnet publish ...FsiHost... -c Release -f net472`
    - `dotnet publish ...Server... -c Release -f net10.0 -r win-x64 --self-contained true`
    - `deploy-remote-services.ps1` PowerShell parse
    - `deploy-remote-services.ps1 ... -WhatIf`
- 結果：
  - `FsiActor.fs` 路徑與 compile ownership 已一致
  - `fsihost` / `fsharp-devkit` service name 可由程式本身正確對應
  - deploy script 可成功 parse，`-WhatIf` 可正確顯示目標主機與遠端路徑
  - `scripts/` 既有 demo 腳本已不再偽裝成可直接執行 MCP
- 風險：
  - 本輪未持有可直接操作的遠端部署主機，因此尚未實做真正的 remote copy / service start end-to-end 驗證
  - `FsiHost` 的 .NET Framework reference warnings 仍存在，屬既有 packaging / reference 整理議題
- 關聯：
  - `doc/Deployment.md`
  - `doc/Runbook.md`
  - `scripts/deploy-remote-services.ps1`
  - `scripts/README.md`
  - `log/20260319140523.deploy-build.op_log`
  - `log/20260319140523.fsihost-publish.op_log`
  - `log/20260319140523.server-publish.op_log`
  - `log/20260319140523.deploy-script-syntax.op_log`
  - `log/20260319140523.deploy-script-whatif.op_log`
