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

## 2026-03-22 12:30:00

- 背景：使用者要求不再把 `fsharp-devkit` 當成單 session toy，而是升級成可支援多 agent / 多 host / 多 session 的 control-plane + execution-plane 架構，且仍需保留舊 MCP client 相容性。
- 動作：
  - 改以 `origin/20260319_001.async-merge` 為 execution baseline，另開 `20260322_001.multi-agent-fsi-platform`
  - 將設計收斂成 `ExecutionBackend` 抽象 + `InProcBackend` / `NetFxRemote` / `Net10Remote`
  - 將 result contract 拆成純 `FsiResult` 與帶 routing metadata 的 `FsiExecutionRecord`
  - 建立 `AgentRegistry`、`HostRegistry`、`SessionRegistry`、`AsyncJobRegistry`、`ResultRegistry`
  - 將 execution 入口統一進 `ExecutionRouter`
- 決策：
  - 保留 dual-backend，而不是強制全量切 out-of-proc
  - 新世界的 host provisioning 僅允許 `netfx/net10`，`inproc` 只保留給 legacy default route
  - session model 一律 `actor per session`
  - `result_op` 一律由 parent / server-side `ResultQueryService` 協調，不進 session actor
- 困境：
  - `async-merge` 有可沿用的 mixed-runtime host，但沒有 control plane，若直接在 `main` 重做會浪費既有驗證
  - `main` 分岔後新文件與 log 需要安全搬回新 branch，且 `notes/00001.txt`、`00002.txt` 不能直接覆蓋
- 解法：
  - 明確區分「沿用 execution plane」與「重切 control plane」
  - `notes` 以新編號方式帶入，避免覆蓋舊 branch 歷史
- 關聯：
  - `doc/Requirement.md`
  - `doc/BA.md`
  - `doc/SA.md`
  - `doc/SD.md`
  - `doc/WBS.md`

## 2026-03-22 14:20:00

- 背景：主線已完成 routed execution / result plane / smoke regression，但若測試只停在 service/tool 層，無法證明 MCP transport 真正可供 agent 使用。
- 動作：
  - 在主專案新增 `McpClientHarness.fs`，用真 `McpClient + StdioClientTransport` 啟動 server 並打 MCP protocol
  - 新增 `McpClientAvailabilityTests.fs`、`McpClientSmokeTests.fs`、`McpClientE2ETests.fs`
  - 參考 `/workspace/home/work/fsharp` 裡 FSI 測試概念，抽出 persistence / implicit `it` / shadowing / reset 等 smoke pattern
- 結果：
  - availability、smoke、E2E 現在都能透過主專案內建 client 驗證
  - 測試不再只是 server-side direct call，而是真的走 `Ping -> tools/resources -> execute/query`
- 困境：
  - 直接在外部 `.fsx` 重用 `McpClientHarness` 時，除了 server/core DLL 還要顯式帶 `ModelContextProtocol.*` 與 logging assemblies
  - 這不是 server 壞掉，而是外部腳本重用組件時沒有自動 deps context
- 解法：
  - 對 repo 內測試先用 `tests/` 專案承接真 client 驗證
  - 對 repo 外用法，明確在 `DEMO.md` 記錄依賴前提與推薦做法
- 關聯：
  - `src/FSharp.MCP.DevKit.Server/McpClientHarness.fs`
  - `tests/McpClientAvailabilityTests.fs`
  - `tests/McpClientSmokeTests.fs`
  - `tests/McpClientE2ETests.fs`
  - `tests/README.md`

## 2026-03-22 15:20:00

- 背景：`WP09/T41` 一直是未完成 stub，`query_fsi_results(language=fsharpCode)` 只能回 `not implemented`，不足以支撐 agent 對歷史 result 做自訂集合運算。
- 動作：
  - 在 `FSIService` 補 `AddBoundValue` 與 `EvaluateExpressionObject`
  - 在 `ResultQueryService` 建立隔離的 query FSI session，綁入 `records1/records2` 與 `primaryRecords/secondaryRecords`
  - 支援兩種查詢形式：
    - 直接 expression
    - `fun records1 records2 -> ...` lambda
  - 補 service-level 與 client-level 測試，驗證 `FSharpCode` query 可回 materialized JSON
- 結果：
  - `query_fsi_results(language=fsharpCode)` 已可對 `ResultId seq` 執行 F# query
  - `built-in` 與 `FSharpCode` query 都能走同一條 result plane
- 困境：
  - `FSharpCodeAnalysis` 並不是 query executor，不能直接拿來做 `T41`
  - 若直接把 quotation object 放上 MCP transport，會讓 execution contract 膨脹且不穩定
- 解法：
  - 保持 transport 收 `string`
  - 在 server 端用受控 FSI session 執行 query，避免污染 execution contract
  - query 結果優先 materialize JSON；若型別無法直接序列化，至少保留人類可讀 `Output`
- 已知後續優化點：
  - `.NET 10` backend 的真正 `ResetSession` 仍需上游 `FAkka.Fsi.Contracts / FAkka.FSI.Supervisor` 增加 reset message，不能只在 server 端偽造成功
  - 若外部 consumer 要直接腳本化重用 `McpClientHarness`，需提供更友善的 sample/bootstrap
- 關聯：
  - `src/FSharp.MCP.DevKit.Core/FSIService.fs`
  - `src/FSharp.MCP.DevKit.Server/ResultQuery/ResultQueryService.fs`
  - `tests/McpResultToolsTests.fs`
  - `tests/McpClientSmokeTests.fs`

## 2026-03-22 15:55:00

- 背景：使用者要求 agent 自行試用一波，再決定還有哪些體驗面需要修整。
- 動作：
  - 以主專案內建 `McpClientHarness` 的 client-based tests 作為第一層 self-use 驗證
  - 另外嘗試在 repo 外以臨時 `.fsx` 與臨時 console app 直接重用 `McpClientHarness`
- 結果：
  - repo 內真 MCP client 路徑可用，availability / smoke / E2E 已驗證
  - repo 外直接重用 `McpClientHarness` 時，會遇到 bootstrap friction：
    - 裸 `#r` 少量 DLL 不足，還需顯式帶 `ModelContextProtocol.*` 與相關相依
    - 臨時 app 若用錯 F# console project 的 entrypoint 形態，也會被額外 tooling 細節干擾
- 判讀：
  - 這不是 server 核心功能不可用，而是「把主專案裡的 client harness 拿到 repo 外直接腳本化」仍不夠順手
  - 因此把它列入 `WBS` 優化 backlog 的 `O2`
- 後續建議：
  - 新增 `samples/` 或獨立 demo client app
  - 在 `DEMO.md` 明確寫出 repo 外 consumer 的依賴前提

## 2026-03-22 22:35:00

- 背景：`.NET 10` 路徑的 `Net10HostBackend.ResetSession` 一直是 stub，代表多 host / 多 session 架構在 net10 host 上缺一個真正可用的 session lifecycle primitive。
- 動作：
  - 在上游 `FAkka.Fsi.Contracts` 新增 `ResetSession / ResetSessionResult`
  - 在上游 `FAkka.FSI.Supervisor` 實作 per-session reset，透過 supervisor 移除 session actor 並回覆 reset result
  - 修正新增 reset contract 帶來的 record literal 型別歧義，將 `GetSessionInfo/ListSessions/Checkpoint/Fork` 相關 request 顯式標註
  - 發布 `FAkka.Fsi.Contracts 10.1.201.1`、`FAkka.FSI.Supervisor 1.562.101.201-dgx.6` 與 `FAkka.Proc.Supervisor 1.562.101.201-dgx.5`
  - 在本 repo 更新 `IFsiSupervisorClient`、`FsiSupervisorClient`、`Net10HostBackend.ResetSession`
  - 補 `Net10HostBackendTests` 與 `SmokeRegressionTests` 的 reset coverage
- 結果：
  - `.NET 10` host reset 已從假實作變成真 contract
  - `O1` 可以正式從 backlog 移出，只剩 `O2-O5`
  - 純 `nuget.org` restore/build/test 已確認可用，不需要把本地 package source 寫進 repo
- 困境：
  - 發版初期 nuget.org index 有延遲，容易讓人誤以為 package 還不存在
  - `FAkka.Fsi.Contracts` 原本是 `Exe` 形態，轉為 library 後需要同步修 pack 與空 `Program.fs`
  - 上游 tests 與 bootstrap helper 大量使用 `{ session = ... }` 這類 record literal，新增同欄位 contract 後很容易被錯誤推斷
- 解法：
  - contracts 專案改為真正 library，並補 Linux `pwsh` post-build/push
  - package push 成功後，再用 flat container 與純 `nuget.org` restore 雙重確認版本可見
  - 將所有受影響 request record 顯式標註型別，避免未來 message 擴充再次踩雷
- 關聯：
  - `src/FSharp.MCP.DevKit.Server/Integration/FsiSupervisorClient.fs`
  - `src/FSharp.MCP.DevKit.Server/Backends/Net10HostBackend.fs`
  - `tests/Net10HostBackendTests.fs`
  - `tests/SmokeRegressionTests.fs`
  - `/workspace/home/work/PulseTrade.fs/Libs/FAkka.Fsi.Contracts/Contracts.fs`
  - `/workspace/home/work/PulseTrade.fs/Libs/Akka.FSI.Supervisor/Supervisor.fs`

## 2026-03-22 23:15:00

- 背景：完成 `.NET 10` reset 後，開始用真 `McpClientHarness + stdio MCP transport` 自試 routed onboarding 與 demo path，結果暴露出幾個 direct-call 測不到的 transport 級問題。
- 動作：
  - 在 `Core` 新增 `FSharpJson`，統一用 `FSharp.SystemTextJson` 處理 F# DU/option/list 的 JSON serialize/deserialize
  - 將 `Program`、control-plane resources、result resources、result tools、client harness 的 JSON 路徑全部切到 `FSharpJson`
  - 修正 `Program` 中 `FsiMcpService` 的 DI 註冊，避免 optional constructor parameter 讓容器誤判 `FSharpOption<bool>` 依賴
  - 新增 `ensure_fsi_route` tool 與 `EnsureRouteResponse` DTO，讓 routed execution onboarding 不必先手動分辨 default route 與已存在 route
  - 將 `query_fsi_results` / `compare_fsi_results` / `list_fsi_results` 的高風險 optional parameter façade 改成 transport-safe string contract
  - 新增 `examples/FSharp.MCP.DevKit.DemoClient`，並補 `DemoClientSmokeTests`
- 結果：
  - 真 stdio MCP client 現在可穩定跑通 `discover`、`legacy-roundtrip`、`ensure-default-route`、`async-roundtrip`、`result-aggregation`
  - `ensure_fsi_route` 現在可作為 routed onboarding helper，但不會偷幫 out-of-proc host provisioning；要建 host 仍必須明確呼叫 `create_fsi_host`
  - `CreatedHost` 對 legacy `default-host` 不再誤報成 `true`
- 困境：
  - direct-call 測試不會暴露 F# optional parameter 與 F# DU JSON 在 MCP binder/serializer 上的相容性問題
  - 真 client 路徑下，若 deserialize 失敗，沒有 raw response 與 stderr 很難定位
- 解法：
  - client harness 在 JSON parse 失敗時附上 raw response + stderr
  - 對高流量 MCP façade 優先收斂成 transport-safe 參數，減少把 F# type system 直接暴露給 JSON binder
  - 將外部 consumer 的第一層體驗改為 demo client，而不是鼓勵從裸 `.fsx` 起手
- 關聯：
  - `src/FSharp.MCP.DevKit.Core/Json.fs`
  - `src/FSharp.MCP.DevKit.Server/McpControlPlaneTools.fs`
  - `src/FSharp.MCP.DevKit.Server/McpResultTools.fs`
  - `src/FSharp.MCP.DevKit.Server/McpClientHarness.fs`
  - `examples/FSharp.MCP.DevKit.DemoClient/Program.fs`
  - `tests/DemoClientSmokeTests.fs`
