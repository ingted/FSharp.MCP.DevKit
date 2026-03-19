# SA

## 任務摘要

- 目標 repo：`FSharp.MCP.DevKit - Async`
- 參考來源：
  - `E:\dk\FSharp.MCP.DevKit`
  - `E:\dk\FSharp.MCP.DevKit - Async - 複製`
- 本輪目標：把 `FSharp.MCP.DevKit` 的 net472 FSI host + .NET 9 MCP server/client mixed-runtime 架構，和 `Async - 複製` 的 incremental code snippet execution / queue-request-then-async-exec 能力合併到 `FSharp.MCP.DevKit - Async`。

## 現況觀察

### A. `FSharp.MCP.DevKit`

- `src/FSharp.MCP.DevKit.Core/FSharp.MCP.DevKit.Core.fsproj` 是 `net472`，並編入 `FsiActor.fs`。
- `src/FSharp.MCP.DevKit.FsiHost/FSharp.MCP.DevKit.FsiHost.fsproj` 是 `net472`。
- `src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj` 是 `net9.0`。
- `src/FSharp.MCP.DevKit.Server/McpFsiTools.fs` 透過 Akka remote actor 連到 `akka.tcp://FsiExecutionSystem@localhost:8081/user/fsiActor`。
- 這版的設計重點正確：FSI session 在 netfx host，MCP server 維持在 .NET 9，兩者用 Akka 溝通。

### B. `FSharp.MCP.DevKit - Async - 複製`

- 已加入 async queue、`execute_f_sharp_code_async`、`fsi/async/{asyncId}` resource 與 HTTP polling endpoint。
- 但這版有三個結構性問題：
  1. `Core/Analysis/Communication/CodeEditing/Documentation` 幾乎都升成 `net10.0`，但 `Server` 仍是 `net9.0;net10.0`、`Tests` 仍是 `net8.0;net9.0;net10.0`，導致 restore 出現 `NU1201`。
  2. `McpFsiTools.fs` 的 remote actor path 改成 `18081`，但 `FsiHost/akka.conf` 仍是 `8081`，host / client 連線參數不一致。
  3. async queue 實作不是走 mixed-runtime host，而是在 server 內直接建立本地 `FsiService`，等於把 netfx host 架構繞掉。

### C. `FSharp.MCP.DevKit - Async` baseline 問題（本輪已修正）

- 目前是半合併狀態：已帶入 multi-target 與 async queue/polling 設計，也保留 Akka 設定與 `FsiHost` 專案。
- 但目前 target repo 仍有以下問題：
  1. `src/FSharp.MCP.DevKit.Server/McpFsiTools.fs` 同時保留 remote actor 與本地 `FsiService + PipeServer + PipeClient` 兩套路徑，session backend 未收斂。
  2. `src/FSharp.MCP.DevKit.Server/McpFsiTools.fs` 第 195-205 行附近的 `EnqueueExecuteCode` 將 `TimeSpan option` 指派到 `Timeout: TimeSpan`，是明顯型別錯誤。
  3. `src/FSharp.MCP.DevKit.Server/McpFsiTools.fs` 的 remote actor path 仍是 `18081`，與 `src/FSharp.MCP.DevKit.FsiHost/akka.conf` 的 `8081` 不一致。
  4. `src/FSharp.MCP.DevKit.Core/FSharp.MCP.DevKit.Core.fsproj` 與 `FsiActor.fs` 的責任邊界混亂；本輪改為 `Core` 只保留 shared API，`FsiActor.fs` 直接移入 `FsiHost` 專案，避免把 Akka host 依賴帶回 shared library。
  5. 三個 repo 的 baseline build 都先被 `csharp-sdk` 的 `NU1903` 卡住，target repo 還伴隨 mixed merge 本身的型別/target framework 問題。

## 問題定義

- 使用者要的是「保留 mixed-runtime 架構」而不是「把 async queue 直接做在 server 內本地 FSI」。
- `Async - 複製` 的 async queue 概念正確，但實作位置錯了，導致：
  - session state 分裂
  - netfx host 名存實亡
  - Akka remote 與 local FSI 兩套行為混雜
- 若不先統一 session backend，再加功能只會把 incremental snippet state、tool state、session state 變成不可追蹤。

## 根因判讀

1. merge 時是把「async queue 功能」直接貼進 `McpFsiTools.fs`，沒有同步把 backend abstraction 收斂成單一路徑。
2. target framework 調整是局部做的，沒有整體檢查 consumer / dependency 相容矩陣。
3. host / server 的 Akka 設定沒有一併校正，出現 `18081` / `8081` 漂移。
4. `Core.fsproj` 與 `Messages.fsproj` 的責任邊界在 merge 過程中被破壞，導致 actor compile path 漏接。

## 範圍

### In Scope

- 讓 `FSharp.MCP.DevKit - Async` 重新成為可描述、可編譯、可驗證的 mixed-runtime repo。
- 保留 net472 `FsiHost` + Akka remote actor 通道。
- 讓 sync / async FSI tools 共用同一個遠端 session backend。
- 將 `Async - 複製` 的 queue-request-then-async-exec / async status polling 併入 mixed-runtime 通道。
- 修正 target framework 相容矩陣與 target repo 內的明顯 merge 錯誤。

### Out of Scope

- 本輪不處理 `csharp-sdk` 上游 `NU1903` 套件漏洞根因；僅以 local build workaround 繞過 restore gate 來驗證本 repo compile。
- 本輪不重做所有 code-editing / documentation tool 的產品設計；只修到與 mixed-runtime FSI backend 一致且可驗證。
- 本輪不主動建立遠端 GitHub / Jira / Confluence 工單；若 `check.fsx` 因 external tracking 要求失敗，再回報使用者補齊。

## 假設

1. mixed-runtime 架構以 `FSharp.MCP.DevKit` 為真相來源。
2. `Async - 複製` 中值得保留的是 async queue / polling / incremental session state，不是它目前那條 local FSI backend。
3. 對使用者最重要的是「同一個遠端 FSI session 能同時支援 sync 與 async snippet execution」，而不是維持目前 target repo 的雙 backend 行為。

## 風險

1. Akka remote DTO 若直接傳 `obj` 或 `FSharpDiagnostic`，可能有跨 runtime 序列化風險。
2. 若部分 tools 仍走 local backend、部分 tools 改走 remote backend，FSI state 會分裂。
3. 若 core target framework 切分不正確，可能再出現 `net472` host 與 `net9/net10` server 的 reference incompatibility。
4. `NU1903` 會遮蔽真正的 compile error，需要用 local build workaround 才能看見 merge 後的真實編譯狀態。

## 分析結論

- 目標 repo 應收斂成「單一 session backend」：server 端所有 session-bound FSI tools 都走 net472 host 的 Akka remote actor。
- async queue 應保留在 server 端，因為：
  - queue / polling / asyncId lifecycle 屬於 MCP server concern
  - 真正執行仍委派給遠端 host，因此能保留 incremental session state
- server 端需要一個 remote client adapter，對上維持既有 tool 邏輯，對下統一轉成 Akka command request。

## 本輪落地結果

- `src/FSharp.MCP.DevKit.Messages` 已建立 transport-safe DTO，避免直接跨 runtime 傳 `obj` / `FSharpDiagnostic`。
- `src/FSharp.MCP.DevKit.FsiHost/FsiActor.fs` 已成為 actor 的唯一實體與 compile owner，並維持 net472 單一 FSI session。
- `src/FSharp.MCP.DevKit.Server/McpFsiTools.fs` 已收斂成 `RemoteFsiClient + async queue/cache`，不再在 server 內自啟本地 `FsiService`。
- `remoteActorPath` 已與 host 固定為 `8081`。
- host / server 讀取 `akka.conf`、`akka.server.conf` 已改為從輸出目錄解析，避免工作目錄漂移。
- `dotnet build FSharp.MCP.DevKit.Async.sln -p:NuGetAudit=false` 已通過。
- mixed-runtime smoke 已通過：
  - sync execute：`let x = 41`
  - async queue：`let y = x + 1`
  - session continuity：`y = 42`
  - package reference：`#r "nuget:Newtonsoft.Json, 13.0.3"`
  - reset regression：reset 後 `y` 不再存在

## 剩餘風險

1. `csharp-sdk` 仍受上游 `NU1903` 影響；本輪 compile 驗證依賴 `NuGetAudit=false` workaround。
2. `Core` 對 `NuGet.* 7.3.0` 仍有 `NU1701` 相容性警告。
3. `FsiHost` 仍有 .NET Framework reference resolution warning，需要後續獨立整理 binding / reference policy。

## 2026-03-19 相依更新補充

- 重新以 audit-enabled build 驗證後，先前記錄的 `csharp-sdk` `NU1903` 已未再重現；目前真正阻塞 audit 的 critical dependency 是 target repo 內直接引用的 `Akka.Remote 1.5.30`。
- `src/FSharp.MCP.DevKit.FsiHost/FSharp.MCP.DevKit.FsiHost.fsproj` 與 `src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj` 已同步將 `Akka` / `Akka.Remote` 升到 `1.5.62`，維持同版本配對。
- 升版後，`dotnet build FSharp.MCP.DevKit.Async.sln` 已不再出現 `NU1904`。
- project-level vulnerability audit 已驗證：
  - `FSharp.MCP.DevKit.FsiHost` 無 vulnerable packages
  - `FSharp.MCP.DevKit.Server` 無 vulnerable packages
- solution-level `dotnet list ... package --vulnerable` 在本 repo 仍可能因 NuGet source mapping / restore 行為差異報 `NU1100`；這是工具鏈檢查路徑差異，不是升版後 package 真正無法 restore，因為同 repo `dotnet restore` 與 `dotnet build` 已成功。

## 2026-03-19 部署與腳本盤點補充

- 使用者要求的部署形態是「給定一台可 PowerShell Remoting 的 Windows 主機與一個程式放置根目錄，腳本自動複製正確 artifact 並註冊兩個服務」。
- 這代表 deployable runtime 邊界必須明確：
  - `fsihost`：`net472` Windows service，負責唯一 FSI session。
  - `fsharp-devkit`：`.NET 10` MCP server Windows service，負責 `/mcp`、`/fsi/async/{asyncId}`、`/healthz`。
- 為讓 SCM 啟動與註冊名稱一致，程式本身也必須支援 service name 設定，不能只靠 `sc.exe create`。
- `scripts/` 目錄盤點結果：
  - `deploy-remote-services.ps1` 是本輪要補齊的正式部署入口。
  - 既有 `fsi-*` 與 `build-packages.sh` 大多是 placeholder / demo，不能視為可直接使用的 MCP client 工具。
- 本輪決策：
  - `FsiHost` 與 `Server` 均支援由命令列傳入 service name。
  - 新增 `scripts/deploy-remote-services.ps1`，支援 publish 或重用既有 artifact、遠端複製、服務註冊、啟動與健康檢查。
  - 將 `scripts/` 既有 placeholder 明確標示為 stub，避免假裝成功導致誤用。

## 關聯追溯

- Local docs:
  - `doc/SA.md`
  - `doc/SD.md`
  - `doc/WBS.md`
  - `doc/Test.md`
  - `doc/DevLog.md`
- External tracking:
  - GitHub tracker: https://github.com/EHotwagner/FSharp.MCP.DevKit/issues
  - 專屬 Jira / PR 仍待補
