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

## 2026-10-02 ProcSupervisor win15 exact consumer

唯一 implementation 改動：`src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj` 的 `FAkka.Proc.Supervisor` `Version="[1.571.101.400-win14]"` → `Version="[1.571.101.400-win15]"`。Server IsPackable=false；不改 Server 版本。SDK global.json 10.0.203 維持；依 root library-packs source 消費同 bytes 本機 candidate。Local unsigned package 尚未 public publish，不稱官方套件或 deployed runtime。Release build 只驗 API/dependency compatibility，不代表 MCP E2E；既有 source/XML/SCM integration evidence 在 Proc repo，production SCM 不操作。
## 2026-10-04 DEP52 infra exact consumer（DEVKIT-DEP52）

只同步 `src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj` 兩個 active exact references：`FAkka.FSI.Supervisor [1.571.101.400-win2]` → `[1.571.101.400-win3]`、`FAkka.Proc.Supervisor [1.571.101.400-win15]` → `[1.571.101.400-win16]`。消費本機已驗 archive/SDK feed，producer source `357fedeef66535b2daacd8c6c3ff235f88d328b2`；不變更 Server PackageVersion1.0.0、API或部署。
SDK維持global.json10.0.203/patch；Server既有FSharp.Core10.1.400不變。建置明傳401 library-packs作AdditionalProjectSources、新C artifacts path、GeneratePackageOnBuild=false/PublishNuGetAfterPack=false/IsPackable=false/DeployOnBuild=false；不執行Server/Proc/FSI或pack。驗實際deps兩版本與輸出DLL對producer proof，不把compile升為runtime/E2E。
時序偏差與操作證據：`log/20261004/20261004052552.aster_dep52_infra_refs.log`。既有兩筆edit先由main prelog覆蓋，本地prelog補於接管後、build前；不倒填已完成操作的事前授權。
## DEVKIT-PROC-WIN21（20261013）

只改 src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj 的 FAkka.Proc.Supervisor exact pin [1.571.101.400-win20]→[1.571.101.400-win21]，以 Proc 專案固定bin的本地nupkg source restore/build，核assets與輸出DLL hash。此slice不改FSharp MCP工具協議、版本、Service image或正式Proc；NuGet公開發布與runtime載入需另驗。

## DEVKIT-PROC-WIN23（20261014）

Server 的 FAkka.Proc.Supervisor exact reference 從 win22 改 win23，以消費 Proc SQL durable queue 的精確 serializer 修復；FAkka.FSI.Supervisor win4 與本 Server API 均不變。restore 使用 Proc 原專案 bin/Release 內的本地 nupkg，build 固定專案 bin/obj。驗 assets 與輸出 DLL 同包內 hash；source compile 不代表 FSI MCP runtime 或正式 Proc 已部署。

## DEVKIT-PROC-WIN24：FSI PCSL 冷重開 package consumer（2026-10-14）

`src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj` 的 FAkka.FSI.Supervisor exact `[1.571.101.400-win4]`→`[1.571.101.400-win5]`、FAkka.Proc.Supervisor exact `[1.571.101.400-win23]`→`[1.571.101.400-win24]`。僅 package references；MCP tool/API、session logic 與 Host 設定不變。restore/build 先用 root 專案本地 Debug packages 驗閉包，再等正式 signed readback；本地 source 成功不能推論正式 runtime 已更新。依賴意圖：IFileSystem10.1.402 constructor 初始 index＋List persisted index → FSI win5 → Proc win24 → Server。失敗時保留原已部署 Server，正式 Proc/MDCQ 不變。

## 2026-10-14 Proc win27 exact consumer

只將 `src/FSharp.MCP.DevKit.Server/FSharp.MCP.DevKit.Server.fsproj` 的 `FAkka.Proc.Supervisor` exact pin由win25升win27，以消費已通過source review的合作式supervisee與lifecycle revision6；FSI win5、MCP tool/API、session與Host設定不變。使用上游固定bin的win27 package restore/build，核assets exact version及輸出DLL同包；不部署runtime，不操作Proc／MDCQ。
