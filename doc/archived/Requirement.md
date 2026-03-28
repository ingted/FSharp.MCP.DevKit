# Requirement Analysis

## 文件資訊

- 日期：2026-03-22
- 來源：
  - `/workspace/ai/instruction/AGENTS.md`
  - `/workspace/ai/instruction/MCP.md`
  - `/workspace/ai/instruction/MCP.KM.md`
  - `/workspace/home/work/mcp/FSharp.MCP.DevKit/doc4dev/20260320_issues.md`

## 問題範圍

本次分析範圍不是業務功能，而是 `agent -> MCP client -> fsharp-devkit MCP server -> FSI session` 這條鏈上的可用性、可觀測性與診斷能力。

本次任務要回答的是：

1. 目前 agent 視角下，真正阻礙穩定工作的核心問題是什麼。
2. 哪些問題已被文件明確證實。
3. 哪些只是懷疑，但目前沒有足夠證據下結論。
4. 後續 BA / SA / SD / WBS 應聚焦在哪些能力補強。

## 利害關係人

1. Agent / MCP client 使用者：需要穩定、低成本、自主完成 F# session 初始化與診斷。
2. fsharp-devkit 維護者：需要知道應優先補哪些 tooling / protocol / diagnostics 能力。
3. 部署與整合維運者：需要降低 path mapping、session reset、timeout 等整合成本。

## 核心需求

1. async-first：對可能超過數秒的 FSI 動作，必須有穩定的 async 使用路徑。
2. diagnostics-first：async 失敗時，必須能直接定位最關鍵的錯誤，而不是只看到 `IsSuccess=false`。
3. session observability：client 需要讀到目前 session 的狀態與組成。
4. integration discoverability：path mapping / mount mapping 必須可查，而不是靠 trial-and-error。
5. agent-friendly contract：tool/resource 回傳格式應足夠結構化，可直接供 agent 記錄、輪詢與恢復。

## 已確認的問題

1. `execute_f_sharp_code_async` 失敗時常缺乏診斷資訊。
   - 依 `20260320_issues.md` 記載，可能只回 `IsSuccess=false`、`Output=""`、`Errors=""`。
   - 這會迫使 agent 退回到一個 assembly / 一個 `open` 的低效率 bisect。

2. 同步工具容易受到外層 transport timeout 影響。
   - 問題不一定在 F# 執行本身，而是 client 對長操作的 timeout 預期不穩定。
   - async 路徑已證明是正確方向，但覆蓋面仍不足。

3. session model 對 client 太隱性。
   - 目前缺乏 `session id`、`loaded assemblies`、`search paths`、`last reset time` 等可讀狀態。

4. async 工具回傳值過於精簡。
   - bare string async id 對 agent 不夠友好，缺乏 `statusUri`、`sessionId`、`submittedAt` 等 metadata。

5. 多個 `#r` / `open` 放在同一 block 時，失敗模式不夠可預測。
   - 文件中的實例顯示，大 block 失敗後往往仍需拆到最小單位才能定位缺依賴。

6. async 路徑下 `printfn` / stdout 不可靠。
   - 這削弱了 agent 以小 probe 判斷 session readiness 的能力。

7. path mapping 對 container client 並非自描述。
   - 目前需要仰賴額外知識才能把 server 看到的路徑對回 container 內真實路徑。

## 懷疑但尚未證實的問題

1. 「大量 `#` 或 `\"` 會讓 agent 在 tool call JSON 轉換時經常壞掉」目前沒有被本次 issue 文件直接證實。
2. 根據目前可得材料，較有證據的是：
   - tool registry / tool availability 曾不同步
   - async diagnostics 不足
   - dependency 順序與缺依賴不易定位
3. 因此在後續 BA 階段，這個議題應被歸類為 `Suspected but not proven`，不能直接當成既定根因。

## 非目標

1. 不處理業務 domain logic。
2. 不先討論 UI / CLI 美化。
3. 不先討論 package 發版流程。
4. 不先做大規模重構；先聚焦 agent / MCP client 視角的高價值可用性問題。

## 成功條件

1. agent 能以少量 probe 快速定位錯誤，而不是靠大量 bisect。
2. 長時間初始化能穩定透過 async 完成。
3. session 狀態與 path mapping 可直接查詢。
4. async 回傳契約足夠結構化，便於 agent 自動輪詢與記錄。
5. 後續 BA / SA / SD / WBS 能明確區分：
   - Confirmed issues
   - Suspected but not proven
   - Integration artifacts / fallback artifacts

## 目前觀測到的前置風險

1. `pulsetrade` 解析 workspace 時失敗，原因是：
   - `examples/ExampleTool/ExampleTool.fsproj` 無法被 `dotnet msbuild` 載入，錯誤為 `Root element is missing`
   - 後續 SA 若要做知識圖分析，需要改採 targeted parse 或先排除此壞掉的 example project
2. 目前目錄不是 Git working tree。
   - 後續若 SOP 需要 git 追溯，需以「目前 workspace 未納 git」這個事實為前提處理

## 下一步建議

下一步進入 BA，將需求分群並排序：

1. Confirmed
2. Suspected but not proven
3. Fallback artifact / integration artifact

並進一步決定：

1. 哪些是功能缺口
2. 哪些是契約缺口
3. 哪些是部署/整合文件缺口
