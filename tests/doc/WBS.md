# Test Project WBS

## WP-T01 Test Project Scaffolding

目標：

讓 tests 專案可以承載 `WP01/WP02` 的回歸案例。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT01 | 更新 tests fsproj project references | 可引用 `Server` | tests 可編譯 | done |
| TT02 | 更新 tests fsproj compile includes | 三個新測試檔 | tests 可編譯 | done |
| TT03 | 保留既有 `SmartSymbolDetectionServiceTests` | 不破壞既有測試入口 | tests 可編譯 | done |

## WP-T02 Backend Selector Tests

目標：

驗證 backend resolve 規則。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT04 | 新增 `BackendSelectorTests.fs` | fake backend + 測試案例 | `Resolve` 規則通過 | done |
| TT05 | 驗證 missing-backend fail fast | 例外測試 | 例外訊息可判讀 | done |

## WP-T03 Backend Adapter Tests

目標：

驗證 mapping 與 metadata helper。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT06 | 新增 `BackendAdaptersTests.fs` | `toFsiResult` 測試 | diagnostics/value 映射正確 | done |
| TT07 | 測 `inferRawErrorType` | error type 測試 | 三種分支可覆蓋 | done |
| TT08 | 測 `toExecutionRecord` | execution record 測試 | metadata 保留正確 | done |

## WP-T04 Remote DTO Contract Tests

目標：

驗證 route-aware DTO 欄位契約。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT09 | 新增 `RemoteMessageContractTests.fs` | DTO contract 測試 | request/result/response 欄位可用 | done |
| TT10 | 補 optional 欄位案例 | None/Some 案例 | 欄位使用語義清楚 | done |

## WP-T05 Execution

目標：

執行並確認回歸穩定。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT11 | 執行 `dotnet test -f net10.0` | 測試結果 | 全數通過 | done |

## WP-T06 Control Plane Tests

目標：

驗證 WP03 的 registry/default-routing 基本行為。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT12 | 新增 `ControlPlaneTests.fs` | default route / explicit route / validation 測試 | WP03 核心行為可回歸 | done |

## WP-T07 InProc Backend Tests

目標：

驗證 WP04 的 in-proc session isolation 與 metadata。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT13 | 新增 `InProcBackendTests.fs` | session isolation / reset / metadata 測試 | WP04 核心行為可回歸 | done |
| TT14 | 重新執行 `dotnet test -f net10.0` | 測試結果 | 新增測試後仍全數通過 | done |

## WP-T08 Routed Integration Tests

目標：

驗證 WP03/WP04 主線整合後，`ExecutionRouter`、`FsiMcpService`、default route 與 async queue 的核心行為。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT15 | 新增 `ExecutionRouterTests.fs` | router/result/session integration 測試 | default route 與 explicit route 可回歸 | done |
| TT16 | 新增 `FsiMcpServiceTests.fs` | default routed execution / async status 測試 | service 主線行為可回歸 | done |
| TT17 | 重新執行 `dotnet test -f net10.0` | 測試結果 | 主線整合後仍全數通過 | done |

## WP-T09 NetFx Backend Tests

目標：

驗證 `NetFxHostBackend` 的 route 傳遞與 remote session snapshot mapping。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT18 | 新增 `NetFxHostBackendTests.fs` | fake remote client 測試 | execute route / session mapping 正確 | done |
| TT19 | 更新 `RemoteMessageContractTests.fs` | `SessionState` 欄位測試 | 新 DTO 契約可回歸 | done |
| TT20 | 重新執行 `dotnet test -f net10.0` | 測試結果 | NetFx backend 補測後仍全數通過 | done |

## WP-T10 MCP Surface Tests

目標：

驗證已切到 routed execution 的主要 FSI tools 與 async status resource。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT21 | 新增 `McpSurfaceTests.fs` | tool/resource 表面測試 | execute/eval/add-path/state 行為可回歸 | done |
| TT22 | 補 async resource 測試 | `FsiResources.AsyncStatus` 測試 | async tool + resource 流程可回歸 | done |
| TT23 | 重新執行 `dotnet test -f net10.0` | 測試結果 | 已完成功能補測後仍全數通過 | done |

## WP-T11 Net10 Backend And Provisioning Tests

目標：

驗證 `WP06` 的 `.NET 10` host backend、proc provisioning 與 session provisioning 契約。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT24 | 新增 `Net10HostBackendTests.fs` | fake supervisor/proc client 測試 | request mapping / state / health / restart 可回歸 | done |
| TT25 | 新增 `ProvisioningServicesTests.fs` | host/session provisioning 測試 | `createHost` / `createSession` 契約可回歸 | done |
| TT26 | 調整 tests 專案到 `.NET 10` MTP 相容設定 | `FSharp.MCP.DevKit.Tests.fsproj` | `dotnet test -f net10.0` 可正常執行 | done |
| TT27 | 重新執行 `dotnet test -f net10.0` | 測試結果 | `WP06` 新增測試後仍全數通過 | done |

## WP-T12 Async Status Metadata Tests

目標：

驗證 `fsi/async/{asyncId}` 與 `GetAsyncExecutionStatus` 已暴露 `ResultId` 與 route metadata。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT28 | 擴充 `FsiMcpServiceTests.fs` | async status metadata assert | `ResultId/AgentId/HostId/SessionId` 可回歸 | done |
| TT29 | 擴充 `McpSurfaceTests.fs` | resource metadata assert | resource 讀取可看到 `ResultId/route` | done |
| TT30 | 重新執行 `dotnet test -f net10.0` | 測試結果 | async metadata 補齊後仍全數通過 | done |

## WP-T13 Control-Plane MCP Surface Tests

目標：

驗證 `WP08` 第一階段的 control-plane tools 與 resources。

| ID | 工作 | 產出 | 驗收 | 進度 |
|---|---|---|---|---|
| TT31 | 新增 `McpControlPlaneToolsTests.fs` | register/create/list/health 測試 | control-plane tool surface 可回歸 | done |
| TT32 | 補 control-plane resources 測試 | agent/host/session/path-mappings resource 測試 | resource surface 可回歸 | done |
| TT33 | 重新執行 `dotnet test -f net10.0` | 測試結果 | `WP08` 第一階段後仍全數通過 | done |

## 執行順序

1. WP-T01
2. WP-T02
3. WP-T03
4. WP-T04
5. WP-T05
6. WP-T06
7. WP-T07
8. WP-T08
9. WP-T09
10. WP-T10

## 備註

- 本 WBS 已擴充覆蓋到 `WP08` 第一階段的 control-plane MCP surface。

## WP-T14 Routed Execution And Result Plane Tests

驗證 `WP08` 第二階段與 `WP09` 第一階段的 routed execution / result plane。

| ID | 工作項目 | 產出 | 驗收標準 | 進度 |
| --- | --- | --- | --- | --- |
| TT34 | 新增 `McpExecutionToolsTests.fs` | explicit route tool 測試 | routed execute/evaluate/reset/async 可回歸 | done |
| TT35 | 新增 `McpResultToolsTests.fs` | result tool/resource 測試 | get/list/query/compare/materialization 可回歸 | done |
| TT36 | 重新執行 `dotnet test -f net10.0` | 測試結果 | `WP08/WP09` 後仍全數通過 | done |
