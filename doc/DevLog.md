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

## 2026-03-25 23:40:00

- 背景：部署中的 `fsharp-devkit` container 雖然 `/healthz` 與 `ProcSupervisor /health` 都正常，但 `GetVesion`、`create_fsi_host`、host/session 隔離仍失敗，必須先把最低層 remoting 與 bootstrap 路徑驗死。
- 動作：
  - 在 `PulseTrade.fs` 新增 `Libs/TestScripts/verify_proc_supervisor_getversion.fsx`，用與部署相近的方式背景啟 `ProcSupervisor`，再以另一個 client `ActorSystem` 直接 `Ask<GetVesion>` / `Ask<GetAllProcInfo>`
  - 用反射比對 client 與 proc-like `ActorSystem` 的 `serialization-identifiers`、`_serializersById`、`FindSerializerForType`
  - 確認 shared `FsPickler` contract serializer 對 `OpCmd`、`OpResult`、`GetAllProcInfo`、`ProcSnapshot` 已正確綁到 `id=199`
  - 針對 `ProcSupervisor` 的 default/bootstrap procnode 啟動邏輯改為 `dotnet exec --runtimeconfig --depsfile ... Akka.Proc.Supervisor.dll --mode procnode ...`
  - 將 `FSharp.MCP.DevKit` 自己的 Akka client config 改為合併 `ContractSerialization.configForAssemblies [ typeof<IMessage>.Assembly; typeof<ProcStartSpec>.Assembly ]`，避免 server/client 兩端 serializer registry 不一致
- 結果：
  - 最低層 `Akka.Remote Ask<GetVesion>` 已在本機背景 `ProcSupervisor` 上驗證成功
  - `ProcSupervisor` 的 default procnode spec 現在會顯式帶 `exec/runtimeconfig/depsfile`
  - `FSharp.MCP.DevKit` server build 與 tests 在升到 `FAkka.Proc.Supervisor 1.562.101.201-dgx.8` 後通過
- 判讀：
  - 先前 `Cannot find serializer with id [199]` 的核心不是 parser，而是 remoting 兩端沒有共享同一份 contract serializer config
  - 先前 container 內 `bootstrap-net10-host` 一直 `stopped` 的核心不是 `fsdevkit.service` 語法，而是 `ProcSupervisor` 內部 default spawn 仍使用 `dotnet /app/Akka.Proc.Supervisor.dll ...`
- 已知殘留：
  - docker repo 的 `fsdevkit.service` 與部署 image 仍需重建，才能真正吃到 `FAkka.Proc.Supervisor dgx.8`
  - 真正部署後的 `proc/fsi supervisor GetVesion` 與 host/session 隔離仍需在新 image 上再驗一次
- 關聯：
  - `src/FSharp.MCP.DevKit.Server/Program.fs`
  - `src/FSharp.MCP.DevKit.Server/McpFsiTools.fs`
  - `src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj`
  - `notes/00031.txt`

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

## 2026-03-25 14:20:00

- 背景：部署中的 `fsharp-devkit` 跑在單獨 container，由 host 的 `fsdevkit.service` 啟動；之後要從另一個 container 直接用 Akka.Remote 腳本打 `ProcSupervisor` / `FsiSupervisor` 做 `GetVesion` 與版本驗證。
- 現象：
  - 用 [verify_proc_fsi_versions.fsx](/workspace/home/work/PulseTrade.fs/Libs/TestScripts/verify_proc_fsi_versions.fsx) 直打 `akka.tcp://proc-system@127.0.0.1:8110/user/proc-supervisor` 會 `AskTimeoutException`
  - 但這不是 `GetVesion` actor contract 壞掉，而是部署拓樸使然
- 根因判讀：
  - 先前 docker service 只對外暴露 `15000:5000`
  - `FSI_PROC_SUPERVISOR_HOST=127.0.0.1`

## 2026-03-25 16:40:00

- 背景：`ProcSupervisor` / `FSI Supervisor` 的 `GetVesion` local actor test 可過，但 `Akka.Remote` direct ask 先是掉成 `JObject`，後續改動態 serializer 後又出現 `Cannot find serializer with id [199]`。
- 先前錯誤判讀：
  - 一度往 parser / probe / MCP surface 上層追
  - 但最小重現後確認真正缺口在 remoting serializer/binding，不是上層 tool
- 動作：
  - 在上游 `FAkka.Fsi.Contracts` 新增 shared `FsPickler`-based `ContractSerializer`
  - 把 `ProcSupervisor` 跨 wire contract 收斂到 `IMessage` binding 範圍
  - `ProcHost` / `WorkerHost` 改在 startup HOCON 注入 serializer 與 binding，而不是只靠 runtime `AddSerializer`
  - `ContractSerializer.FromBinary` 改以 `obj` 還原 root payload，對齊 FsPickler 這條 wire format 的 `System.Object` 外層
  - 補 `Akka.Proc.Supervisor.Tests` 的 remote `GetVesion/GetAllProcInfo`
  - 補 `Akka.FSI.Supervisor.Tests` 的 remote `GetVesion`
  - 發布：
    - `FAkka.Fsi.Contracts 10.1.201.3`
    - `FAkka.FSI.Supervisor 1.562.101.201-dgx.8`
    - `FAkka.Proc.Supervisor 1.562.101.201-dgx.7`
  - 本 repo 升級到這組新 package
- 結果：
  - `ProcSupervisor` 的 direct remote ask 已不再是 `JObject` / serializer-id failure
  - 上游 `Proc/FSI Supervisor` 測試綠燈
  - `FSharp.MCP.DevKit` 現在吃的是帶 shared remoting serializer 的 package 線
- 判讀：
  - 這次真正修掉的是底層 remoting contract，不是再往 `/send` 或 parser 疊 workaround
  - 之後若部署端仍有 host/session 問題，優先看 deployment/runtime wiring，而不是再懷疑 `GetVesion` typed actor message 本身
  - `FSI_PROC_SUPERVISOR_PATH=akka.tcp://proc-system@127.0.0.1:8110/user/proc-supervisor`
  - 這組設定只對「同一個 container 內」成立
  - 當 caller 在另一個 container 時，`127.0.0.1` 指向 caller 自己，不是 `fsharp-devkit` 那個 container；即使 HTTP MCP 可用，Akka actor path 仍會 timeout
- 困境：
  - 若繼續沿用 bridge networking + container loopback，任何跨 container 的 direct Akka debug script 都會失效
  - 只 publish `8110` 也不夠，因為 `ProcSupervisor` 若仍綁 loopback，外部 port forward 一樣不會通
- 解法：
  - 將 [fsdevkit.service](/workspace/home/mcp/docker/FSharp.MCP.DevKit/fsdevkit.service) 改為 host networking
  - 由 service 在啟動時解析 host IP：
    - 預設 `hostname -I | awk '{print $1}'`
    - 可用 `/etc/default/fsdevkit` 的 `FSDEVKIT_HOST_IP` 覆蓋
  - 將 `ASPNETCORE_URLS` 固定為 `http://0.0.0.0:15000`
  - 將 `FSI_PROC_SUPERVISOR_HOST / WEB_HOST / PATH` 全部改成可從其他 container 到達的 host IP
- 結果：
  - `fsharp-devkit` 與 `ProcSupervisor` 不再只對 container loopback 可見
  - 後續版本驗證腳本與 cross-container Akka debug 流程有穩定入口
- 關聯：
  - `/workspace/home/mcp/docker/FSharp.MCP.DevKit/fsdevkit.service`
  - `/workspace/home/work/PulseTrade.fs/Libs/TestScripts/verify_proc_fsi_versions.fsx`
  - `notes/00024.txt`
  - `notes/00025.txt`
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

## 2026-03-26 11:15:00

- 背景：部署中的 `proc-supervisor` 已能 direct `GetVesion`，但 bootstrap procnode 一直 `stopped`，導致沒有 `fsiSupervisorPath`，因此 `fsi-supervisor` 版本探針與 host/session 驗證都上不去。
- 現象：
  - `docker logs fsharp-mcp-devkit` 持續出現：
    - serializer id `6` `Akka.Remote.Serialization.MessageContainerSerializer`
    - serializer id `12` `Akka.DistributedData.Serialization.ReplicatorMessageSerializer`
  - 這代表 child proc 已經越過 `runtimeconfig/depsfile` 問題開始真正啟動，但在 cluster/sharding traffic 上仍缺 Akka 預設 serializer。
- 根因：
  - `Akka.Proc.Supervisor.ProcHost.loadConfig` 只有 shared contract serializer 與 sharding/singleton fallback。
  - procnode 實際還需要：
    - `Akka.Remote.Configuration.Remote.conf`
    - `Akka.DistributedData.DistributedData.DefaultConfig()`
  - 缺這兩份時，procnode 會在收到 replicator/sharding message 前就 fail 掉，因此 registry 只看到 `stopped` 的 bootstrap snapshot。
- 解法：
  - `ProcHost.loadConfig` 改為顯式 fallback：
    - remote default config
    - distributed-data default config
    - cluster sharding default config
    - cluster singleton default config
  - `Akka.Proc.Supervisor.Tests` 新增真正的 bootstrap regression：
    - `dotnet exec ... --mode supervisor --spawndefault`
    - 輪詢 `/api/proc/nodes`
    - 驗證 bootstrap procnode 出現非空 `fsiSupervisorPath`
  - `FAkka.Proc.Supervisor` 升版為 `1.562.101.201-dgx.9`
- 驗證：
  - `dotnet fsi Libs/Akka.Proc.Supervisor/test_scripts/verify_bootstrap_procnode.fsx`
    - `PASS`
    - nodes JSON 內已出現非空 `fsiSupervisorPath`
  - `dotnet test Libs/Akka.Proc.Supervisor.Tests/Akka.Proc.Supervisor.Tests.fsproj --filter "...GetVesion...|...Bootstrap procnode...|...dotnet exec..." -m:1`
    - `3 passed, 0 failed`
- 判讀：
  - 目前最底層已經收斂成：
    - deployed `proc-supervisor` 可 direct ask
    - local bootstrap procnode 可活著註冊 `fsiSupervisorPath`
  - 下一步只剩把 `FSharp.MCP.DevKit` 升到 `FAkka.Proc.Supervisor dgx.9` 並重部署，再重新驗 deployed `fsi-supervisor GetVesion` 與 host/session 隔離。

## 2026-03-26 11:50:00 Correction

- 背景：
  - 使用者指出 `notes/00033.txt` 承載了 deployment / diagnosis 類 operational finding。
  - 依 `AGENTS.md`，這類資訊應該進 `log/` 與 `doc/DevLog.md`，不應只停留在 `notes/`。
- 補正：
  - 將 `notes/00033.txt` 的核心內容正式回填到：
    - `log/20260326115000.修正將部署診斷從notes移回DevLog與log.00001.00001.log`
    - `log/20260326115000.修正將部署診斷從notes移回DevLog與log.op_log`
    - 本段 `DevLog Correction`
  - 既有 `notes/00033.txt` 保留不動，視為當時的暫存摘錄；後續不再把 deployment / diagnosis 類內容單獨留在 `notes/`。
- 判讀：
  - `notes/` 適合閱讀摘錄與探索片段。
  - deployment / diagnosis / root-cause / command evidence 應固定落在：
    - `log/`：任務級證據
    - `DevLog.md`：長期知識沉澱
- 行動準則更新：
  - 之後若有類似 operational finding，先寫 `log/.op_log`，收斂後 append 到 `DevLog.md`。
  - 若一度先記到 `notes/`，必須在同一輪內補回正式追溯產物，不可讓 `notes/` 成為唯一證據來源。

## 2026-03-26 12:45:00 Correction

- 背景：
  - 在前一輪 correction 收尾時，誤把新的 `push/check` 輸出 append 到既有 `log/20260326115000...op_log`。
  - `check.fsx` 因此指出：
    - `log_readonly_modify`
    - `latest_log_not_advanced`
- 根因：
  - 我把 `.op_log` 當成可持續追加的作業筆記使用，違反了本 repo 對 `log/` 的唯讀規範。
  - 收尾第二輪應建立新的 `.log/.op_log`，而不是續寫上一筆。
- 補正：
  - 新增：
    - `log/20260326124500.修正續寫既有oplog並補新任務log.00001.00001.log`
    - `log/20260326124500.修正續寫既有oplog並補新任務log.op_log`
  - 後續若要補記 `push/check` 或二次收尾，固定開新 log，不再改既有 log 檔。

## 2026-03-26 14:35:00 Deployment Verification

- 背景：
  - 使用者要求停止高層推測，只驗最底層 deployed `fsharp-devkit` remote host/session 隔離是否真的可用。
  - 驗證方式改成：
    - 不走 MCP
    - 不走 `/send`
    - 直接對 deployed `proc-supervisor` / `fsi-supervisor` 做 `Akka.Remote Ask`
- 已確認事項：
  - `proc-supervisor GetVesion` 成功。
  - `GetAllProcInfo` 成功。
  - `ProcSupervisor` 在 deployed host 上可 direct `StartProc` 成功建立新的 procnode。
  - `bootstrap-net10-host` 已不再是必要前提；改由 actor-level `StartProc` 驗證新 host。
- host isolation 驗證：
  - 直接建立兩個 remote procnode：
    - `deploy-host-a-actor`
    - `deploy-host-test-single`
  - 對兩邊同名 session `shared-session` 分別執行：
    - host A: `let hostValue = 101`
    - host B: `let hostValue = 202`
  - 再各自讀回：
    - host A -> `101`
    - host B -> `202`
  - 結論：deployed remote host isolation 成立。
- session isolation 驗證：
  - 在同一個 deployed remote host 下，使用兩個不同 session：
    - `verify-session-a`
    - `verify-session-b`
  - 分別執行：
    - session A: `let sessionValue = 111`
    - session B: `let sessionValue = 222`
  - 再各自讀回：
    - session A -> `111`
    - session B -> `222`
  - 結論：deployed remote session isolation 成立。
- 注意事項：
  - 先前 direct `StartProc` 超時，不等於 child proc 啟動失敗；這次重新觀察後，`StartProc` 可能成功但 ask 在 coordinator 壓力下未及時回覆。
  - operational 驗證應固定落在 `log/` 與 `DevLog.md`，不再回寫到 `notes/`。

## 2026-03-26 15:50:00 MCP Tool Binding Fix

- 背景：
  - 使用真正的 HTTP MCP client 直打 deployed `fsharp-devkit` 時，`create_fsi_host` 只回 generic error：
    - `An error occurred invoking 'create_fsi_host'.`
  - container log 裡的真 exception 是：
    - `System.ArgumentException: The arguments dictionary is missing a value for the required parameter 'probeMessage'.`
- 根因：
  - `McpControlPlaneTools.CreateFsiHost` 對外暴露的是 F# optional parameters：
    - `?arguments`
    - `?workingDirectory`
    - `?hostId`
    - `?probeMessage`
    - `?probeIntervalMs`
  - ModelContextProtocol 的 reflection binder 對這種 F# optional surface 沒有正確降成「可省略」的 tool arguments，省略 `probeMessage` 時被當成缺必填欄位。
- 修法：
  - 將 `McpControlPlaneTools` 的 public tool surface 改成 CLR optional/default-value 參數：
    - `RegisterFsiAgent.displayName`
    - `CreateFsiHost.arguments/workingDirectory/hostId/probeMessage/probeIntervalMs`
    - `CreateFsiSession.sessionId/sessionName`
    - `GetFsiPathMappings.agentId/hostId`
  - method 內再把：
    - `null` / 空字串 -> `None`
    - `0` -> `None`
  - 也就是說，MCP reflection binder 看到的是正常 CLR optional 參數，而 service 層仍保留原本的 option semantics。
  - `probeMessage` / `probeIntervalMs` 的語意補充：
    - 它們不是 host creation 的核心必填欄位。
    - 它們是 `ProcSupervisor` 對 child proc 做週期性健康檢查時用的 probe 設定。
    - `probeMessage`：要送進 proc 的探測字串，例如 `listsessions --all true`。
    - `probeIntervalMs`：探測週期。
    - 省略時代表：
      - host 仍可建立與使用；
      - 但 `ProcSupervisor` 不會額外替這個 proc 啟用 active probe。
    - 因此這兩個欄位在工具層應該是 optional，不應被 MCP binder 當成 create host 的必填參數。
- 驗證：
  - 本地：
    - `dotnet build src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj -c Release -m:1`
    - `dotnet test tests/FSharp.MCP.DevKit.Tests.fsproj -f net10.0 --filter "FullyQualifiedName~McpControlPlaneToolsTests" -m:1`
    - 均通過。
  - live deployment：
    - 重新 build docker image 並重起 container 後，
    - 直接使用 MCP tools 成功完成：
      - `register_fsi_agent`
      - `create_fsi_host`
      - `create_fsi_session`
      - `execute_f_sharp_code_routed`
      - `evaluate_f_sharp_expression_routed`
  - 結論：
    - 先前的 generic error 不是 runtime/procnode/fsi-supervisor 的問題，而是 MCP reflection binder 與 F# optional tool signature 的相容性問題。
  - 後續驗證補充：
    - 將 `probeMessage` / `probeIntervalMs` 完全省略後，live deployment 不再拋出 `missing probeMessage`。
    - 新的真 exception 變成：
      - `Akka.Actor.AskTimeoutException: Timeout after 5.00 seconds`
      - 位置在 `ProvisioningServices.CreateHost`
    - 這證明：
      - binder bug 已修掉；
      - 後續若還有 generic error，應歸因到 host provisioning/ask timeout，而不是 MCP optional parameter binding。

## 2026-03-26 16:45 UTC ProcNode Address Propagation Follow-up

- Reconfirmed the live problem is no longer generic MCP binding or shell-like argument tokenization.
- Current live behavior after ProcSupervisor `dgx.11` redeploy:
  - `create_fsi_host` succeeds far enough to create a proc entry and pid.
  - Child procnode log reports remoting up and `FsiSupervisorActor ready`.
  - Parent `ProcSnapshot` still remains `starting` with missing `nodeAddress`/`fsiSupervisorPath`, so MCP host records retain `address = null`.
- The next diagnostic cycle is narrowed to `ProcNodeActor` state propagation, not the higher MCP layer.

## 2026-03-26 16:56 UTC ProcSupervisor Local Package Payload Mismatch

- The live container reported `Akka.Proc.Supervisor.dll` informational version `dgx.10` while `FSharp.MCP.DevKit.deps.json` referenced package `dgx.11`.
- Inspecting the locally built `FAkka.Proc.Supervisor.1.562.101.201-dgx.11.nupkg` showed the packaged DLL itself still embedded `dgx.10`.
- Running `dotnet clean -c Release && dotnet build -c Release` in `Libs/Akka.Proc.Supervisor` corrected both the Release DLL and its `.deps.json` to `dgx.11`.
- Conclusion: the previous dgx.11 local package payload was stale; host docker rebuilds needed the corrected local package bits before live address-propagation validation meant anything.

## 2026-03-26 17:43 UTC MCP Host Provisioning Timeout Recovery

- Live deployment diagnostics showed a split between two ProcSupervisor query paths:
  - `StartProc` / `GetProcInfo` go through the sharding region and could throw `AskTimeoutException`.
  - `ListProcInfo` goes to the registry-backed aggregate view and already showed the new proc snapshots while MCP tools were still returning generic host/probe errors.
- Concrete symptom:
  - `create_fsi_host` timed out even though `/api/proc/nodes` already contained the new host proc id with `running` status.
  - `get_fsi_host_health` also timed out on direct `GetProcInfo`.
- Fixes applied in `FSharp.MCP.DevKit`:
  - `HostProvisioningService` now resolves host snapshots through:
    - `GetProcInfo(hostId)` first
    - fallback to `ListProcInfo() |> find ProcId = hostId` on timeout or missing snapshot
  - `Net10HostBackend.HealthCheck` uses the same fallback path.
- Separate MCP tool-surface fix:
  - routed execution tools in `McpExecutionTools` still used F# optional parameters (`?timeoutSeconds`) and the MCP reflection binder treated omitted values as required arguments.
  - those methods were changed to CLR optional/default-value parameters so the HTTP MCP surface can omit `timeoutSeconds`.
- Local verification:
  - targeted tests for:
    - `ProvisioningServicesTests`
    - `Net10HostBackendTests`
    - `McpExecutionToolsTests`
    all passed after the fallback and binder changes.
- Operational conclusion:
  - once runtime/procnode execution had been proven healthy, the remaining `create_fsi_host` / `get_fsi_host_health` failures were orchestration-level timeout handling defects, not remote FSI capability defects.

## 2026-03-26 18:05 UTC Live MCP Verification After Timeout-Recovery Fix

- Deployed the updated server image onto the host by rebuilding the docker image from `/home/sa/gemini4/mcp/docker/FSharp.MCP.DevKit`.
  - `build.host.sh` successfully rebuilt the image but still required interactive `sudo` for service reinstall.
  - For immediate validation, the running `fsharp-mcp-devkit` container was replaced manually with the same `docker run` arguments as the service definition.
- Live MCP verification was performed through `McpClientHarness.createHttpClientAsync` against:
  - `http://10.28.112.140:15000/mcp`
- Verified behaviors:
  - `create_fsi_host` now succeeds even when `probeMessage` / `probeIntervalMs` are omitted.
  - `create_fsi_session` succeeds on the new remote host.
  - routed execution/evaluation also succeeds when `timeoutSeconds` is omitted from the MCP arguments.
  - same-host, multi-session isolation is working through the actual MCP tool path:
    - `session-a -> 111`
    - `session-b -> 222`
  - `get_fsi_host_health` no longer throws a generic timeout error; it returns a normal health payload (`starting` while the host is still converging).
- Product conclusion:
  - remote host creation, remote session creation, and routed remote execution are now functioning end-to-end through the deployed MCP HTTP surface.

## 2026-03-28 Issue File Reconciliation

- 將 `doc4dev/20260328_issues.md` 由單次 review 快照更新為帶狀態註記的文件。
- 已確認修正者，補 `NOTE`：
  - Net10HostBackend 的 stale Reset/GetState 訊息
  - routed execution 錯誤上下文補強
  - ProvisioningServices polling 加入 bounded recovery
  - ListFsiResults 的 hostId/sessionId 訊息已改善
- 已確認仍 open 者，保留 `NOTE`：
  - `invalidOp` 濫用
  - `Console.WriteLine` 殘留
  - `/healthz` 只報 process 狀態
  - `FSI_PROC_SUPERVISOR_TIMEOUT` 硬編碼
  - actorSystem terminate ignore
  - SearchInFile 20 筆截斷提示不夠醒目
- 新增 ISSUE-017：remote host 看到的 volume path 與 agent container path 不同，導致 `#I` / `#r` 非 NuGet DLL 解析失敗。這是今天實際重現到的主要 agent 使用痛點，性質偏 Runbook/操作指引，而非核心 runtime failure。

## 2026-03-28 Severity-First Fixes

- 不再接受 `doc4dev/20260328_issues.md` 中的模糊狀態字眼，開始按 P0/P1 直接落實修正。
- `FsiMcpService.ExecuteOperation` 現在對 `GetState` / `ResetSession` / `RestartHost` 做 direct dispatch：
  - 不再透過 `ExecutionRouter -> backend.Execute` 的假路徑繞一圈再拿 unsupported record。
  - `GetFsiState(Routed)` 會回真正的 session state 字串。
  - `ResetFsiSession(Routed)` 會呼叫 backend reset，然後同步刷新 session registry。
- `ExecutionRouter.RouteAndExecute` 新增 faulted session 前置攔截：
  - 若 session registry 已知 `SessionFaulted`，對一般執行型 operation 直接回清楚、可行動的錯誤。
  - 錯誤內含 `PreviousFailedResultId`，避免再等 upstream 回那句模糊的 `Operation could not be completed due to earlier error`。
- `Program.fs` 補上：
  - `akka.server.conf` 缺失時的空 fallback，不再直接 `ReadAllText` 崩掉。
  - `FSI_PROC_SUPERVISOR_TIMEOUT` / `--proc-supervisor-timeout-seconds` 支援。
  - application stopping 時 `actorSystem.Terminate().GetAwaiter().GetResult()`，不再直接 ignore。
- `search_in_file` 現在在第一行就標示總數與顯示筆數，例如 `Found N occurrence(s) ... (showing first 20)`。
- 驗證：
  - `dotnet test tests/FSharp.MCP.DevKit.Tests.fsproj -f net10.0 --no-restore --filter "FullyQualifiedName~McpExecutionToolsTests|FullyQualifiedName~McpSurfaceTests|FullyQualifiedName~FsiMcpServiceTests|FullyQualifiedName~SmokeRegressionTests" -m:1`
  - 通過。

## 2026-03-28 Remote Evaluate Troubleshooting Start
- Focus narrowed to a product bug after mount/path mismatch was corrected: remote `execute_f_sharp_code_routed` can report success while subsequent `evaluate_f_sharp_expression_routed` in the same session does not see earlier bindings.
- Current hypothesis: the net10 remote backend/supervisor path is dispatching `EvaluateExpression` through the same interaction API as `ExecuteCode`, or otherwise not preserving evaluation semantics the same way the in-proc backend does.
- Next action: inspect `Net10HostBackend`, `FsiSupervisorClient`, and `Akka.FSI.Supervisor` message handling before touching MCP surface or docs again.

## 2026-03-28 Real Agent Workflow Re-test Against Deployed Container

- 背景：
  - 重新以真正的 agent 使用情境驗證：
    - 建 remote host
    - 建 remote session
    - 執行 `generate_real_charts.inspect_930k_vs_30k.fsx` 前 76 行
    - 再 evaluate `cfar.Cfarta...c`
  - 目標是判斷這是產品 bug，還是單純 container volume / path 問題。
- 實驗步驟：
  - 先釐清 deployed `gemini4` container 與 `fsharp-devkit` container 的 volume 對應：
    - agent 常看到 `/workspace/home/...`
    - remote host 實際可見的是 `/gemini4/...`
  - 將腳本中的 `#I` / `#r` 路徑改寫成 remote container 可見路徑。
  - 之後分別驗證：
    - sync routed execute
    - async routed execute
    - 後續 evaluate
- 結果：
  - volume/path mismatch 已確認是第一個主要 agent 使用痛點。
  - 只要直接把 agent container 路徑原樣送進 remote host，會表現成：
    - search path 不存在
    - 非 NuGet DLL 找不到
    - session faulted
  - 這不是 runtime capability failure，而是部署路徑映射缺少明確操作指引。
  - 修正路徑後，remote host/session 本身仍可用。
  - 但長時間 workload 若走 `execute_f_sharp_code_routed`，在 live deployment 仍可能出現：
    - `AskTimeoutException`
    - `EndpointDisassociatedException`
  - 同一批 workload 改走 `execute_f_sharp_code_async_routed` 後，`fsi/async/{asyncId}` 輪詢保持健康，後續 evaluate 路徑明顯較穩。
- 結論：
  - `fsharp-devkit` 的 remote host/session capability 本身仍成立。
  - 真正需要補強的是：
    - Runbook 對 container 路徑映射的說明
    - 對長腳本的 async-first agent 指引
  - 同步 routed execute 對長 workload 的穩定性仍是 open product issue，不應再和 volume path mismatch 混為一談。
  - `async-first` 的意思不是換一個 session，也不是換一個 execution semantics。
    - sync / async 都是在同一個 remote host、同一個 remote session 中執行
    - 差別只在 control path：
      - `execute_f_sharp_code_routed` 會讓 caller 同步等待單次 ask 完成
      - `execute_f_sharp_code_async_routed` 只負責 enqueue，之後由 `fsi/async/{asyncId}` 輪詢最終狀態
    - 對 heavy workload 而言，目前已知較脆弱的是「同步等待這條 ask 路徑」，不是 session binding 本身
    - 所以建議流程才是：
      1. async execute
      2. 等完成
      3. 在同一 session evaluate expression 取值

## 2026-03-28 Doc Hygiene And Agent-Facing E2E Scenario Guide

- 將與目前專案現況偏離過大的舊規劃/分析文件移至 `doc/archived/`：
  - `Action.md`
  - `BA.md`
  - `Ideation.md`
  - `Policy.md`
  - `Requirement.md`
  - `SA.md`
  - `SD.md`
  - `Test.md`
- 保留在 `doc/` 根目錄的文件收斂為目前仍直接服務開發/部署/追溯的內容：
  - `Deployment.md`
  - `DevLog.md`
  - `Runbook.md`
  - `WBS.md`
- 新增 `doc/E2EScenarioTest.md`，專門給其他 LLM Agent 執行真實 remote FSI 案例。內容明確規定：
  - 讀取 `generate_real_charts.inspect_930k_vs_30k.fsx` 的第 1~76 行後，只能修改「送入 MCP 的字串內容」，不可修改原始 `.fsx`
  - 移除 `#if INTERACTIVE` / `#endif`
  - 將 `#I "/workspace/home/..."` 改為 remote host container 可見的 `/gemini4/...`
  - 在送入片段前先設定 `SHARFTRADE_PCSL_ROOT=/gemini4/vhdx/cFar_pcsl2/cFar2`
  - 對此長 workload 採 async-first：`execute_f_sharp_code_async_routed -> poll fsi/async/{asyncId} -> evaluate_f_sharp_expression_routed`
  - 若 agent 的 MCP tool call 無法使用，改以純 HTTP MCP JSON-RPC 依序完成 `initialize -> notifications/initialized -> tools/call -> resources/read`
- 這份文件的目的不是重複 Runbook，而是提供單一、可照抄、可替換 host/session/script-path 的 E2E 操作劇本，避免其他 agent 再把路徑/mount 問題誤判成 runtime bug。

## 2026-03-28 Single-Shot Gemini CLI Validation Attempt

- 依使用者要求，參考 `notes/gemini_exec.txt`，以單次 headless 模式驗證 Gemini CLI 是否能讀取 `doc/E2EScenarioTest.md` 並完成遠端 host/session 任務。
- 實際使用指令重點：
  - `gemini -m gemini-3.1-pro-preview --approval-mode yolo -p "<prompt>"`
  - 已確認 `gemini mcp list` 顯示 `fsharp-devkit` server connected。
- 驗證策略：
  - 只執行一次。
  - 若失敗，依使用者要求立即停止，不做第二次嘗試或 prompt 微調。
- 實際結果：
  - Gemini CLI 尚未開始執行 scenario，就在模型呼叫階段收到：
    - `HTTP 429`
    - `MODEL_CAPACITY_EXHAUSTED`
    - `No capacity available for model gemini-3.1-pro-preview on the server`
- 判讀：
  - 這次失敗不能拿來判斷 `E2EScenarioTest.md` 是否不夠清楚。
  - 也不能拿來判斷 prompt 是否設計不良。
  - 根因是 Gemini 服務端當下無法提供指定模型容量。
- 後續原則：
  - 這次依要求立即停止，不再追打 Gemini。
  - 若要進一步區分「md 問題」還是「prompt 問題」，需等模型容量恢復後再做下一次、仍然單次且可歸因的驗證。

## 2026-03-28 Clarified Container Mount Semantics For Agent Guidance

- 補強 `doc/E2EScenarioTest.md` 的路徑說明，避免 agent 再把 `fsharp-devkit` container 內的兩種 mount 混為一談：
  - `/gemini4/...` 對應 host `/home/sa/gemini4/...`，為 **唯讀** source-tree / DLL / 腳本視角
  - `/workspace/...` 對應 host `/home/sa/gemini4/devkit_workspace/...`，為 **可讀寫** workspace 視角
- 對 agent 的操作意義：
  - 讀 `.fsx`、讀非 NuGet DLL、讀 source tree 時，應優先使用 `/gemini4/...`
  - 需要 remote host / server 寫暫存檔或中間產物時，應使用 `/workspace/...`
  - 不應把 agent container 看到的 `/workspace/home/...` 直接視為 remote host 可見路徑

## 2026-03-28 Gemini Retry Outcome: md Improved, Next Failure Is Async Resource Visibility

- 先以 `gemini-3.1-pro-preview` 重試同一份 prompt 與同一份 `doc/E2EScenarioTest.md`。
  - 結果仍是 `HTTP 429 / MODEL_CAPACITY_EXHAUSTED`
  - 因此 `3.1` 這條仍無法用來判讀文件品質。
- 再以完全相同的 prompt 與文件改用 `gemini-2.5-pro`。
- 結果：
  - Gemini 2.5 能成功讀懂文件
  - 能辨識「tool surface 不一定直接有 `resources/read`，因此要改走純 HTTP fallback」
  - 能完成：
    - fresh host 建立
    - fresh session 建立
    - async code submit
  - 失敗點改為：
    - 輪詢 `fsi/async/{asyncId}` 時，持續得到 `exists=false`
    - 最終 timeout
- 判讀：
  - 這表示 `doc/E2EScenarioTest.md` 的前一輪修正是有效的：
    - path rewrite 規則有被遵守
    - Python/HTTP fallback 方向被採納
    - agent 不再首先死在 shell quoting
  - 下一個 failure surface 已經不是 prompt/文件理解，而是：
    - `fsharp-devkit` 在純 HTTP fallback + async polling 這條路上的 async resource 可見性/保留行為
  - 因此後續若要繼續修，應優先查：
    - 為何 agent 取得 `asyncId` 後，`resources/read` 仍回 `exists=false`
    - 是 server 端 async registry/resource surface 問題，還是 Gemini 的 HTTP 讀取/解析流程與 MCP server 預期不完全一致

## 2026-03-28 SA/SD: Add `get_async_status` Tool To Decouple Weak Agents From `resources/read`

- 新增 fresh 的 [SA.md](/workspace/home/mcp/FSharp.MCP.DevKit/doc/SA.md) 與 [SD.md](/workspace/home/mcp/FSharp.MCP.DevKit/doc/SD.md)，把問題明確定義為：
  - server 並不缺 `fsi/async/{asyncId}` resource
  - 真正缺的是「弱 client / 弱 agent 對 `resources/read` 的可用性」
- 設計決策：
  - 新增 MCP tool `get_async_status(asyncId)`
  - 不分 routed/default，因為 async job 本來就只以 `asyncId` 為查詢主鍵
  - resource 仍保留，tool 只是給較弱 agent 的 ergonomics 補洞
- 具體實作：
  - [McpFsiTools.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/McpFsiTools.fs) 新增 `get_async_status`
  - async execute 的工具描述改為明確提示：
    - 優先輪詢 `get_async_status`
    - 或讀 `fsi/async/{asyncId}`
  - [Program.fs](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/Program.fs) 的 async resource 描述同步更新
- 測試：
  - [McpSurfaceTests.fs](/workspace/home/mcp/FSharp.MCP.DevKit/tests/McpSurfaceTests.fs) 驗證 tool 與 resource 的 async status payload 對齊
  - [McpExecutionToolsTests.fs](/workspace/home/mcp/FSharp.MCP.DevKit/tests/McpExecutionToolsTests.fs) 驗證 routed async 可經 `get_async_status` 完成輪詢
  - [McpClientAvailabilityTests.fs](/workspace/home/mcp/FSharp.MCP.DevKit/tests/McpClientAvailabilityTests.fs) 驗證 discoverability
  - [McpClientSmokeTests.fs](/workspace/home/mcp/FSharp.MCP.DevKit/tests/McpClientSmokeTests.fs) 改用 `get_async_status` 做 async smoke
- 驗證結果：
  - `dotnet build src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj -m:1` 通過
  - 相關 targeted tests（surface/execution/client availability/client async smoke）通過

## 2026-03-28 Live HTTP Reproduction Loop: Gemini `exists=false` Was Not Reproduced

- 為了回答「為什麼我之前能成功、Gemini 卻失敗」，刻意不部署新 build，而是直接對 live deployment 做純 HTTP JSON-RPC 重現，盡量貼近 Gemini 當時的 fallback 路徑。
- Attempt 1：
  - 結果：失敗於 `initialize`
  - 症狀：`HTTP 406 Not Acceptable`
  - 根因：文件中的 Python fallback 範例漏了 `Accept: application/json, text/event-stream`
  - 判讀：這是文件/範例 bug，不是 server async 狀態面壞掉
- Attempt 2：
  - 結果：收到 initialize response 但 client 誤判沒有 session id
  - 症狀：`Missing mcp-session-id`
  - 根因：server 回的是 `Mcp-Session-Id`；範例先把 headers 轉成普通 dict，再只抓小寫 `mcp-session-id`
  - 判讀：這也是文件/範例 bug，而不是 server 沒給 session id
- Attempt 3：
  - 在補上正確 `Accept` header 與 case-insensitive session header 讀取後，純 HTTP 路徑可成功：
    - `register_fsi_agent`
    - `create_fsi_host`
    - `create_fsi_session`
    - `execute_f_sharp_code_async_routed`
  - `resources/read fsi/async/{asyncId}` 的每一次輪詢都回：
    - `exists = true`
    - `status = Running`
    - `isCompleted = false`
  - 在約 60 秒輪詢視窗內未完成，但 **完全沒有重現 Gemini 先前的 `exists=false`**
- 結論：
  - 目前沒有證據顯示 server async registry / resource surface 會像 Gemini 那次描述的那樣立即消失
  - 更強的解釋是：Gemini 當時的 HTTP fallback 細節有誤，或對 header / session / request 內容處理不正確
  - 對這個 workload，本案例文件還必須再強調：
    - HTTP fallback 必帶 `Accept: application/json, text/event-stream`
    - `mcp-session-id` 要 case-insensitive 讀取
    - `exists=true,status=Running` 持續數十秒不是失敗，而是應增加輪詢預算
- 已同步更新 [E2EScenarioTest.md](/workspace/home/mcp/FSharp.MCP.DevKit/doc/E2EScenarioTest.md)：
  - 修正 Python fallback headers
  - 修正 session header 讀取
  - 增加長 workload 輪詢規則
  - 新增 `get_async_status` 優先策略
  - 新增單次 prompt 範本

## 2026-03-29 Gemini Single-Shot Learning Loop After Redeploy

- 先確認 redeployed `fsharp-devkit` 已包含 `get_async_status`，對 live MCP `tools/list` 的結果為：
  - `tool_count = 57`
  - `has_get_async_status = true`
- 接著開始 `gemini` 單次執行迴圈，優先嘗試 `gemini-3.1-pro-preview`，並把每次結果寫入 `log/20260329.gemini-single-shot-learning.op_log`。
- Attempt 7:
  - `gemini-3.1-pro-preview` 這次不是容量錯誤，已能讀 `E2EScenarioTest.md` 與目標 `.fsx` 前 76 行。
  - 但模型接著自行轉去使用 `generalist` 類委派工具，沒有完成任務。
  - 判讀：這是模型/提示服從性的問題，不是 `fsharp-devkit` server 問題。
- Attempt 8:
  - `gemini-2.5-pro` 能正確讀文件、讀腳本，並開始依文件轉換 code。
  - 失敗點是它嘗試使用 `write_file` 這類本地工具把 transformed code 落到暫存檔，但 Gemini CLI 當前可用的本地工具面只看到 `read_file`、`cli_help`、`generalist`，沒有 `write_file`。
  - 判讀：此時卡住的不是 MCP server，也不是 prompt 對 remote host/session 的說明，而是 Gemini CLI 本身沒有文件假設中的本地寫檔/殼層工具。
- 目前結論：
  - `E2EScenarioTest.md` 已足以教會 agent 理解 remote path rewrite、async-first 與 `get_async_status`。
  - 但若目標 client 沒有本地 `write_file` / shell 能力，就不能直接照目前的 HTTP fallback 版本執行。
  - 下一步應討論是否需要再補一份「無本地寫檔、無 shell」變體流程，專供 Gemini CLI 這類較弱 client。

## 2026-03-29 Added Gemini-Specific E2E Scenario Guide

- 新增 [E2EScenarioTest_gemini.md](/workspace/home/mcp/FSharp.MCP.DevKit/doc/E2EScenarioTest_gemini.md)。
- 這份文件明確針對 Gemini CLI 的實際限制：
  - 常只有 `read_file`
  - 有 `fsharp-devkit` MCP tools
  - 不一定有 `write_file`
  - 不一定有 shell
  - 可能亂用 `generalist` / delegation
- 因此 Gemini 版流程改成：
  - 只用 `read_file` + MCP tools
  - 不依賴本地暫存檔
  - 不依賴 shell
  - 直接把 transformed F# code string 送進 `execute_f_sharp_code_async_routed`
  - 之後用 `get_async_status` 輪詢
  - 完成後再 `evaluate_f_sharp_expression_routed`
- 只有在 agent 明確擁有本地寫檔與 shell 時，才回頭參考一般版 [E2EScenarioTest.md](/workspace/home/mcp/FSharp.MCP.DevKit/doc/E2EScenarioTest.md) 的純 HTTP fallback。

## 2026-03-29 Gemini CLI Prompt Loop Reached A Client-Side MCP Registration Blocker

- 在加入 Gemini 專用文件後，繼續做單次執行 loop：
  - Attempt 9 (`gemini-3.1-pro-preview`)
    - 已不再亂用 `generalist`
    - 也能讀 `E2EScenarioTest_gemini.md` 與目標腳本
    - 但讀完後停滯，不往下打 MCP tools
  - Attempt 10 (`gemini-2.5-pro`)
    - 明確知道應先 `register_fsi_agent`
    - 但仍把這一步轉去 `generalist`
    - 被 Gemini 本地 executor 的 recursion guard 擋掉
  - Attempt 11 (`gemini-2.5-pro`, 最小 direct-tool probe)
    - 不再要求整個 scenario，只要求直接呼叫 `register_fsi_agent`
    - Gemini 最終真的嘗試 direct tool：
      - `fsharp_devkit.register_fsi_agent`
    - 但客戶端回：
      - `Tool "fsharp_devkit.register_fsi_agent" not found`
      - 可見工具只像是本地 `read_file` / `list_directory` / `grep_search`
- 這表示目前阻塞已不是文件理解問題，也不是 `fsharp-devkit` server 本身問題，而是：
  - **Gemini CLI 雖然顯示 MCP server connected，但模型可見的 tool registry 裡沒有真正的 `fsharp-devkit` MCP tools**
- 結論：
  - 目前不應再繼續透過 prompt 微調硬凹
  - 後續應改查：
    - Gemini CLI 的 MCP 註冊/暴露方式
    - 是否需不同 invocation mode / 設定檔 / server naming 方式，才能讓 MCP tools 真正進入模型工具表

## 2026-03-29 Gemini CLI MCP Config Verification

- 依官方文件再次核對 Gemini CLI 的 MCP 設定方式：
  - Gemini CLI 使用 `~/.gemini/settings.json`
  - 不是 `~/.gemini/mcp_servers.json`
- 本機實際狀態：
  - `~/.gemini/settings.json` 已正確包含：
    - `mcpServers.fsharp-devkit.url = http://10.28.112.140:15000/mcp`
  - `gemini mcp list` 也顯示：
    - `fsharp-devkit ... Connected`
- 另外驗證了官方推薦的 `@server-name` 提示方式：
  - 使用 `@fsharp-devkit ...`
  - Gemini 仍然沒有直接使用 `fsharp-devkit` 的 MCP tools
  - 反而只使用本地 `list_directory` / `grep_search` 等工具
- 最小 direct-tool probe 的結果更明確：
  - Gemini 嘗試呼叫 `fsharp_devkit.register_fsi_agent`
  - 但客戶端本地工具表回覆 `Tool ... not found`
- 判讀：
  - 目前阻塞已不是 `fsharp-devkit` server 沒 expose tools
  - 也不是設定檔放錯位置
  - 更像 Gemini CLI headless 模式下，MCP server 雖顯示 connected，但其 tools 沒真正進入模型可呼叫 registry

## 2026-03-29 csharp-sdk Snapshot Reset And Gemini CLI Headless MCP Reality Check

- `csharp-sdk` 的本機修改只有：
  - `Directory.Packages.props`
  - `nuget.config`
- 已先切 snapshot branch：
  - `20260329_snapshot`
  - commit `9ed6b63`
- 之後已切回 `main` 並 reset/pull 到 upstream：
  - `498de08 Release v1.2.0 (#1472)`

- `PulseTrade` 套件鏈重新核對後的 source 版本：
  - `PersistedConcurrentSortedList.IFileSystem 10.0.201`
  - `FAkka.FSI.Supervisor 1.562.101.201-dgx.14`
  - `FAkka.Proc.Supervisor 1.562.101.201-dgx.18`
- local pack / NuGet push 結果：
  - 這幾個版本都已存在於 NuGet，push 實際上是 duplicate/skip，不是新的 publish failure

- `FSharp.MCP.DevKit` 目前 source 參考：
  - `FAkka.FSI.Supervisor 1.562.101.201-dgx.14`
  - `FAkka.Proc.Supervisor 1.562.101.201-dgx.18`
- restore/build 已通過，但 tests 目前仍有既有失敗，至少包含：
  - `InProcBackendTests.InProcBackend executes multi-interaction batches separated by terminators`
  - `SmokeRegressionTests.Smoke old tools remain compatible on default route`
  - `RealNet10HostIsolationTests.Real out-of-proc net10 hosts keep state isolated`
  - `RealNet10HostIsolationTests.Real out-of-proc net10 host executes multi-interaction batches`
  - `McpClientE2ETests.MCP client E2E runner executes all smoke scenarios without failures`
  - `McpClientSmokeTests.Client smoke covers fsharp-code result query`
- 所以目前狀態是：
  - package chain 可 rebuild
  - 但 `FSharp.MCP.DevKit` 測試集不是全綠

- Gemini CLI 0.35.3 的本機 source/registry 驗證推翻了前面一個錯判：
  - `~/.gemini/settings.json` 是正確設定位置
  - `gemini mcp list` 顯示 `fsharp-devkit` connected
  - 更重要的是，直接用 Gemini CLI 自己的 `loadCliConfig(...); config.initialize()` 建出 headless `Config` 後：
    - tool registry 內確實有 `57` 個 `fsharp-devkit` MCP tools
    - 例如：
      - `mcp_fsharp-devkit_register_fsi_agent`
      - `mcp_fsharp-devkit_execute_f_sharp_code_async_routed`
      - `mcp_fsharp-devkit_get_async_status`
- 這表示：
  - **Gemini CLI headless mode 並不是「看不到 MCP tools」**
  - 先前 `Tool "fsharp_devkit.register_fsi_agent" not found` 的 probe，本質上是用了錯的 tool name
  - 正確 fully-qualified name 是 `mcp_fsharp-devkit_*`

- 再做最小 headless 真實驗證：
  - prompt 明確要求只呼叫 `mcp_fsharp-devkit_register_fsi_agent`
  - 結果 Gemini CLI 不再說 tool not found，反而真的開始走 MCP tool path
  - 但它仍然選錯工具，實際跑到了 `mcp_fsharp-devkit_ensure_fsi_route`
  - 最後命令在 `timeout 40s` 下結束
- 這表示目前更準確的現況是：
  - 設定沒有錯
  - MCP tools 也有載入
  - 真正的問題是 **model 在 headless prompt 下仍會錯選/亂選工具，或在 broad prompt 下停住**

- 外部參考：
  - Gemini CLI 官方文件確認使用 `~/.gemini/settings.json` 與 `mcpServers` 設定，不是 `mcp_servers.json`
  - 官方 issue `#12362 Headless Mode Hangs During Execution` 也顯示 headless 模式確實有已知掛住案例
- 因此目前的工程結論是：
  - `fsharp-devkit` 不需要為 Gemini CLI 的「MCP tools 沒載入」背鍋
  - 後續應聚焦在：
    - Gemini 專用文件/Prompt 如何更強制使用正確 FQN
    - 或 Gemini CLI / model 在 headless 模式下的工具選擇穩定性

## 2026-03-29 Package Version Bump For Redeploy

- 依使用者要求，先把本機 `csharp-sdk` 釘回新主線：
  - repo: [csharp-sdk](/workspace/home/mcp/csharp-sdk)
  - branch: `main`
  - HEAD: `498de08 Release v1.2.0 (#1472)`
- local snapshot 先保留在：
  - branch: `20260329_snapshot`
  - commit: `9ed6b63`

- 本輪實際升版：
  - [PersistedConcurrentSortedList.IFileSystem.fsproj](/workspace/home/work/PulseTrade.fs/Libs/PersistedConcurrentSortedList.IFileSystem/PersistedConcurrentSortedList.IFileSystem.fsproj)
    - `10.0.201` -> `10.0.201.1`
  - [Akka.FSI.Supervisor.fsproj](/workspace/home/work/PulseTrade.fs/Libs/Akka.FSI.Supervisor/Akka.FSI.Supervisor.fsproj)
    - `1.562.101.201-dgx.14` -> `1.562.101.201-dgx.15`
    - 依賴 `PersistedConcurrentSortedList.IFileSystem 10.0.201.1`
  - [Akka.Proc.Supervisor.fsproj](/workspace/home/work/PulseTrade.fs/Libs/Akka.Proc.Supervisor/Akka.Proc.Supervisor.fsproj)
    - `1.562.101.201-dgx.18` -> `1.562.101.201-dgx.19`
    - 依賴 `FAkka.FSI.Supervisor 1.562.101.201-dgx.15`
  - [FSharp.MCP.DevKit.Server.fsproj](/workspace/home/mcp/FSharp.MCP.DevKit/src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj)
    - `FAkka.FSI.Supervisor 1.562.101.201-dgx.15`
    - `FAkka.Proc.Supervisor 1.562.101.201-dgx.19`

- 發版結果：
  - `PersistedConcurrentSortedList.IFileSystem 10.0.201.1` -> NuGet push 成功
  - `FAkka.FSI.Supervisor 1.562.101.201-dgx.15` -> NuGet push 成功
  - `FAkka.Proc.Supervisor 1.562.101.201-dgx.19` -> NuGet push 成功

- 驗證結果：
  - `FSharp.MCP.DevKit.Server` restore 成功
  - `FSharp.MCP.DevKit.Server` release build 成功
- 目前仍保留的 warnings：
  - `Suave 3.2.3` 對 `FSharp.Core < 10.1.0` 的 `NU1608`
  - `NuGet.* 7.3.0` 在 `netstandard2.0` core 專案上的 `NU1701`
  - `McpFsiTools.fs` 多個 `FS3511`
- 這輪的目標是讓使用者可重新部署 `FSharp.MCP.DevKit`，不是把整個 test matrix 收到全綠；既有 failing tests 仍待另外處理。

## 2026-03-30 Gemini 3.1 Headless Loop Attempts 14-20

- 延續前一輪已成功的 `attempt13`：
  - Gemini 3.1 已能完成：
    - `register_fsi_agent`
    - `create_fsi_host`
    - `get_fsi_host_health`
    - `create_fsi_session`
    - `get_lines` 讀 `/gemini4/...` 外部 `.fsx`
    - `execute_f_sharp_code_async_routed`
    - `get_async_status` 一次
  - 當時拿到：
    - `agentId = gemini-e2e-agent-001101-a13`
    - `hostId = gemini-e2e-host-001101-a13`
    - `sessionId = gemini-e2e-session-001101-a13`
    - `asyncId = b5b01c5fe6ed468badde6b60443b8961`

- 這一輪目標只剩：
  - 讓 Gemini 3.1 在 headless mode 下，對既有 `asyncId` 做輪詢並在完成後 `evaluate`

- 嘗試摘要：
  - `attempt14`
    - prompt 要求 `get_async_status` 最多 6 次後 evaluate
    - 結果：
      - 模型確實連續呼叫 `get_async_status`
      - 但 async job 持續 `Running`
      - Gemini 自己在 repeated same-tool polling 下觸發 loop recovery，最後 abort
  - `attempt15`
    - 改成單次 `get_async_status`，若未完成則立即輸出 JSON
    - 結果：成功
  - `attempt16`
    - 再收斂成「單次 status check + raw compact JSON + 無 prose/無 markdown fence」
    - 結果：成功
  - `attempt17`
    - 同一 prompt，隔一段時間再查
    - 結果：成功，但仍 `Running`
  - `attempt18`
    - 同一 prompt，期間有 `gemini-3.1-pro-preview` 429 capacity noise
    - 結果：即使有 429，最終仍完成一次 `get_async_status` 並輸出 JSON；仍 `Running`
  - `attempt19`
    - 同一 prompt
    - 結果：成功，但仍 `Running`
  - `attempt20`
    - 同一 prompt
    - 結果：成功，但仍 `Running`

- 產品層結論：
  - 這 7 次嘗試已經足以證明：
    - Gemini 3.1 headless **現在已經學會正確使用 `fsharp-devkit` MCP tools**
    - 對本案例而言，剩下的阻塞不是 prompt
    - 而是該 async 業務腳本在整個觀察窗口內始終維持 `Running`
  - 因此不應再把這個案例誤判成：
    - `Gemini 不會用工具`
    - `MCP tools 沒暴露`
    - `prompt 不夠強`

- 工程結論：
  - 對 Gemini 3.1 headless，穩定可用的操作模式是：
    1. 第一回合：
       - 建 host / session
       - 讀外部 `.fsx`
       - 做 path rewrite
       - `execute_f_sharp_code_async_routed`
    2. 後續回合：
       - 每回合只做一次 `get_async_status`
       - 若完成才 `evaluate`
  - 不建議讓 Gemini 在單一回合內做 repeated polling，因為容易撞到它自己的 loop recovery

## 2026-03-30 01:30:00 Long Running CFar Investigation

- 目標：
  - 釐清 `generate_real_charts.inspect_930k_vs_30k.fsx` 前 76 行在 deployed `fsharp-devkit` 上長時間 `Running`，到底是產品故障還是業務初始化本來就重。

- 先驗：
  - deployed `fsharp-devkit` 核心 MCP 路徑仍正常：
    - `register_fsi_agent`
    - `create_fsi_host`
    - `create_fsi_session`
    - routed execute / evaluate
    - `get_async_status`
  - 先前 direct self-test 仍可在 fresh host/session 內得到：
    - `x = 42`
    - `y = 43`

- 本輪觀察對象：
  - `asyncId = a9753a43091d45c98315290be9d8f8dd`
  - `hostId = codex-investigate-host-1774833523`
  - `sessionId = codex-investigate-session-1774833523`
  - 舊的 Gemini async：
    - `asyncId = b5b01c5fe6ed468badde6b60443b8961`
    - `hostId = gemini-e2e-host-001101-a13`

- 直接結果：
  - 兩個 async job 都仍回：
    - `status = Running`
    - `exists = true`
    - `isCompleted = false`
  - `get_fsi_host_health` 對兩個 host 都回：
    - `isAvailable = true`
    - `message = running`

- 關鍵發現：
  - 這不是 idle `Running`。
  - `docker top fsharp-mcp-devkit` 顯示：
    - `codex-investigate-host-1774833523` 的 procnode 持續高 CPU
    - `%CPU` 一度超過 `200`
  - `docker logs fsharp-mcp-devkit` 顯示該 procnode 正在連續執行大量 CFar/TA 初始化工作：
    - 多組 `[Start] ...`
    - 對應 `[cfTA] root: /gemini4/vhdx/cFar_pcsl2/cFar2/...`
    - 對應 `[Finished] ...`
  - 也出現過：
    - `heartbeat was delayed`
    - 這表示重 CPU / thread starvation 噪音確實存在
  - 但最近 5 分鐘 log 顯示它仍在穩定前進，不像死鎖。

- 結論：
  - 目前最合理的判讀是：
    - `fsharp-devkit` 核心產品沒有壞
    - 問題不是 MCP tool exposure
    - 問題也不是 Gemini prompt
    - 這個案例在第 76 行 `let cfar = CFar(...)` 就已進入非常重的資料初始化
    - 因此 async 可能需要顯著較長時間才會完成
  - 後續若要改善，不是先改 prompt，而是評估：
    - 是否要把這種長時間 CPU work 與目前 actor/default dispatcher 更清楚隔離
    - 或至少在 Runbook / Scenario doc 明寫這個案例屬於 minutes-scale async，不是 quick async

- 追加驗證：
  - 依照真正業務目標，不再重跑初始化，而是盯住同一個 remote session 的既有 async 初始化：
    - `asyncId = a9753a43091d45c98315290be9d8f8dd`
  - 額外再追約 5 分鐘（每 15 秒輪詢一次）後，仍持續：
    - `Running`
    - `isCompleted = false`
  - 因此目前還無法進到「初始化完成後連續查詢 3 次」這一步。

- 這一步的結論修正：
  - 產品問題的核心不是「session 無法重用」。
  - 核心是：
    - 這個特定 `CFar(...)` 初始化在目前資料量下，完成時間遠超一般 quick async 預期。
  - 若要達到「一次初始化，後續連續查 5 次」的體驗，接下來要處理的是：
    - 初始化本身耗時是否可接受
    - 或是否需要把初始化與後續查詢拆成更長壽、更明確的工作流，而不是一個短觀察窗內期待完成

- 同 container 對照實驗：
  - 直接在 deployed `fsharp-mcp-devkit` container 內，對同一份腳本做與 remote session 相同的 rewrite：
    - `#I` 由 `/workspace/home/...` 改成 `/gemini4/...`
    - `SHARFTRADE_PCSL_ROOT` 設為 `/gemini4/vhdx/cFar_pcsl2/cFar2`
    - 僅執行前 76 行
  - 直接 `dotnet fsi /tmp/cfar_remote_compare.fsx` 的完成時間為：
    - `5m29.289s`
  - 而同一份 rewritten payload 經 `fsharp-devkit -> remote host/session -> Akka.FSI.Supervisor` 執行，對應 async job 在兩個多小時後仍保持：
    - `Running`
    - `isCompleted = false`

- 追加結論：
  - 這已經足以排除「只是腳本本身需要很多分鐘」的單一解釋。
  - 在同一個 deployed container 內，直接 `dotnet fsi` 可以在約 5 分半完成；
    但 remote session 路徑沒有在合理時間內收斂。
  - 因此目前可以明確下判斷：
    - `fsharp-devkit` remote execution path 確實存在效能或收斂問題
    - 問題不在 async registry 缺少 completed write-back
    - 問題更可能位於：
      - `Akka.FSI.Supervisor` 的 `FsiSession.Handle/EvalInteraction` 路徑
      - 或其周邊遠端 host/session 執行模型

## 2026-03-30 04:26:48Z Remote CFar Root Cause Narrowed to Interaction Batching

- 目的：
  - 判斷長時間 `CFar(...)` 初始化在 remote session 中遲遲不完成，究竟是：
    - workload 本身很重，
    - 還是 `Akka.FSI.Supervisor` 的 execute path 在大段 script-like source 上有 barrier。

- 關鍵對照一：
  - 在 deployed `fsharp-mcp-devkit` container 內，直接 `dotnet fsi /tmp/cfar_remote_compare.fsx`
  - 完成時間：
    - `333899 ms`（約 `5m34s`）

- 關鍵對照二：
  - 同一份 rewritten source，直接用 `FsiSession.Handle.EvalInteraction` 整段送進去
  - `timeout 420s` 仍未完成
  - 結果：
    - `EXIT=124`

- 關鍵對照三：
  - 同一份 rewritten source，不走單一 interaction
  - 改成：
    - 保留既有 flush 規則
    - 並在 top-level paragraphs 邊界額外 flush
  - 在容器內以 `FsiSession.Handle` 逐 chunk `EvalInteraction`
  - 完成時間：
    - `316735 ms`（約 `5m17s`）
  - 並且後續 expression 查詢成功：
    - `EXPR_RESULT=Choice1Of2`
    - 查詢表達式：`cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c`

- 明確結論：
  - `EvalInteractionNonThrowing` 在語法上不需要 `;;`；官方文件明確支持沒有 `;;` 的 top-level code。
  - 這次問題不是語法需求，而是我們自己的 batching 策略過粗。
  - 現行 `splitInteractionBatch` 幾乎只靠既有 flush marker 分段，會把大量 script-like top-level code 合成單一巨型 interaction。
  - 對這個 `CFar(...)` 案例，單一巨型 `EvalInteractionNonThrowing` 會形成明顯 barrier。
  - 問題不是 async registry、不是 `get_async_status`、也不是「沒有接回 fsi evaluate 結果」。
  - 問題點就是：
    - `Akka.FSI.Supervisor/FsiWorker.splitInteractionBatch`
    - 對大型 `.fsx` 風格 source 的分塊策略過粗。

- 已完成修正（source）：
  - `splitInteractionBatch` 新規則：
    - 保留原本的 flush 行為
    - 若遇到空白行，且下一個非空行縮排回到 top-level（indent = 0），則 flush
  - 這能保留：
    - type / function 內部的縮排區塊
  - 同時把：
    - top-level paragraphs
    - `let cfar = ...`
    - 後續 query
    拆成更接近 `.fsx` 語意的 interaction chunks。

- 現況：
  - source fix 已做完，基礎測試/編譯通過。
  - 尚未重新打包部署到 live `fsharp-devkit`，因此 live server 仍未吃到這個 batching 修正。

- 附註：
  - 額外做「更新後 supervisor 整體路徑」本機驗證時，撞到一個獨立問題：
    - `MBrace.FsPickler.FsPicklerTextSerializer.UnPickleOfString(...)` method mismatch
  - 這是另一條依賴問題，不是這次 remote `CFar` 卡住的主因。

- package / build 狀態：
  - 已升版：
    - `FAkka.FSI.Supervisor 1.562.101.201-dgx.16`
    - `FAkka.Proc.Supervisor 1.562.101.201-dgx.20`
  - `FAkka.FSI.Supervisor dgx.16` 已成功 push 到 NuGet。
  - `FAkka.Proc.Supervisor dgx.20` 已成功 push 到 NuGet。
  - `FSharp.MCP.DevKit` 已改成引用：
    - `FAkka.FSI.Supervisor 1.562.101.201-dgx.16`
    - `FAkka.Proc.Supervisor 1.562.101.201-dgx.20`
  - 本地 `FSharp.MCP.DevKit` release build 已通。
  - 由於 NuGet propagation lag，`FAkka.Proc.Supervisor dgx.20` / `FAkka.FSI.Supervisor dgx.16` 在正式 feed 上短時間內可能還查不到；
    本地驗證時使用了臨時 `RestoreConfigFile=/tmp/fsharp-devkit-local-fakka.config` 指向新產出的 nupkg。
  - 正式 Docker build 仍應只使用 repo 正式 `nuget.config` 與正式來源；等 NuGet propagation 完成後再重部署。

## 2026-03-30 08:37:04 Session Cache Dependency Bump

- `FSharp.MCP.DevKit` 的上游依賴已改為：
  - `FAkka.FSI.Supervisor 1.562.101.201-dgx.17`
  - `FAkka.Proc.Supervisor 1.562.101.201-dgx.21`
- 目的不是功能面新改，而是讓下次部署時吃到 `FAkka.FSI.Supervisor` 本輪對 `ListSessions/GetSessionInfo` 的 session cache 修正。

## 2026-03-30 08:40:13 Release Chain Closure For Session Cache Fix

- `FAkka.FSI.Supervisor dgx.17` 已確認存在於 NuGet；再次 `nuget push --skip-duplicate` 得到 duplicate，表示 package 已在 feed。
- `FAkka.Proc.Supervisor dgx.21` 已成功 build 並 push 到 NuGet。
- `FSharp.MCP.DevKit.Server` 指向：
  - `FAkka.FSI.Supervisor 1.562.101.201-dgx.17`
  - `FAkka.Proc.Supervisor 1.562.101.201-dgx.21`
- 正式 feed 當下仍未完全 propagation 到 `Proc dgx.21`，因此本地 compile 驗證採：
  - 不修改 repo 內 `nuget.config`
  - 不新增新的 `nuget.config`
  - 將 `dgx.17/dgx.21` nupkg 展開到 NuGet global-packages cache
  - 然後以 repo 正式 `nuget.config` 完成 restore/build
- 驗證結果：
  - `dotnet restore src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj --configfile nuget.config`
  - `dotnet build src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj -c Release --no-restore -m:1`
  - 皆已通過

## 2026-03-30 09:35 UTC - Storage Fallback Release Follow-up

- `PulseTrade.fs` 尚有未提交的 `Akka.FSI.Supervisor` runtime 修正，內容為：
  - default PCSL storage root fallback
  - `WorkerHost` 改走統一 bootstrap
- 因此 package chain 再上調為：
  - `FAkka.FSI.Supervisor 1.562.101.201-dgx.18`
  - `FAkka.Proc.Supervisor 1.562.101.201-dgx.22`
- `FSharp.MCP.DevKit.Server` package references 已同步跟進，確保後續部署吃到完整版本而不是只拿到 session-cache 後的一半修正。

## 2026-03-30 15:01 UTC - CreateSession Bootstrap Timeout Recovery

- live `mod2` 測試已確認：
  - `create_fsi_host` 成功
  - proc REST `/api/proc/nodes/{hostId}/sessions` 能看到新 session，狀態為 `busy`
  - 但 `create_fsi_session` 仍因 bootstrap `Execute("()")` 的 `AskTimeoutException` 在 30 秒後直接失敗
  - failure 之後 session 未寫入 MCP `sessionRegistry`，導致後續 routed execute/evaluate 全部被 routing 層拒絕
- 根因在 `SessionProvisioningService.CreateSession`：
  - recovery 輪詢 `GetSessionState` 只會在 `backend.Execute` 正常返回後執行
  - 當 `backend.Execute` timeout、但 session actor 其實已存在時，沒有收斂機會
- 修正方向：
  - 將 bootstrap `Execute("()")` 的 `AskTimeoutException` 視為可恢復事件
  - 即使 execute timeout，仍持續短時間輪詢 `GetSessionState`
  - 只要 session state 變成非 `SessionMissing`，就 upsert 到 `sessionRegistry` 並返回

## 2026-03-30 17:00 UTC - mod2 fresh-image E2E verified against docker run

- 使用全新 `docker build --no-cache` 產生的 `fsharp-mcp-devkit:test-mod2` image，未使用 `docker cp`。
- 手動 `docker run` 掛載與 `fsdevkit.service` 相同：
  - `/home/sa/gemini4:/gemini4:ro`
  - `/home/sa/gemini4/devkit_workspace:/workspace`
- 直接以 MCP Streamable HTTP 完整執行：
  1. `initialize`
  2. `notifications/initialized`（notification，無 `id`）
  3. `register_fsi_agent`
  4. `create_fsi_host`
  5. `create_fsi_session`
  6. 分 4 段 `execute_f_sharp_code_routed` 執行目標腳本 1..76 行
  7. `evaluate_f_sharp_expression_routed`
- 結果：
  - `cfar.Cfarta....c` 成功回傳 `PersistedConcurrentSortedList.CSL2+ConcurrentSortedList...`
- 結論：
  - 目前 `mod2` 可以保留，不需要退回 sparse baseline
  - 真正需要留下的是 `FAkka.FSI.Supervisor` 的 worker bootstrap 修正

## 2026-03-30 23:57 UTC - McpClientHarness build compatibility with latest csharp-sdk

- 使用最新 `csharp-sdk` rebuild `FSharp.MCP.DevKit.Server` 時，`McpClientHarness.fs` 的 `ResizeArray<string * obj>().Add(...)` 在目前 F#/SDK 組合下觸發 `FS0503`。
- 修正：
  - `src/FSharp.MCP.DevKit.Server/McpClientHarness.fs`
  - 將 `EnsureRouteAsync` 內部 pairs collection 改為 plain list，再填入 `Dictionary<string,obj>`。
- 驗證：
  - `dotnet build src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj -c Release -f net10.0 -m:1`
  - 結果 `0 Error(s)`。
- 結論：
  - 這是 build-time 相容性問題，不是 `fsharp-devkit` runtime / MCP routing 問題。

## 2026-04-16 DevKit WinAgent Import Idempotency

- WinAgent envelope import now uses `ResultId` as an idempotency key for the same route.
- Same `ResultId` + same `agentId/hostId/sessionId`: returns existing record, no duplicate output publish.
- Same `ResultId` + different route: fails fast to prevent cross-session pollution.
- Tests: `McpResultToolsTests` 12/12 after isolated store fix.
