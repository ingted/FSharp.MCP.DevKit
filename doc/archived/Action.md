# Action

## 分析

| 任務 | 驗收 | 狀態 |
|---|---|---|
| baseline review 三版 repo | 有 SA 問題列表與 build baseline | done |
| 收斂 merge 假設 | mixed-runtime 為主幹、async queue 併入遠端 backend | done |

## 規劃

| 任務 | 驗收 | 狀態 |
|---|---|---|
| 完成 SA / SD / WBS | 文件可作為實作依據 | done |
| 完成 Test / Policy / Action / DevLog | 文件可支援開發與稽核 | done |

## 開發

| 任務 | 依賴 | 驗收 | 狀態 |
|---|---|---|---|
| 擴充 Messages remote DTO | SA / SD | actor/server 可共用 transport contract | done |
| 修正 Core target framework 與 FsiActor compile path | SA / SD | host compile path 成立 | done |
| 導入 server remote client adapter | 前兩項 | MCP tools 不再依賴 local FSI backend | done |
| 修正 async queue / timeout / polling | remote client adapter | async execute 可運作 | done |
| 修正 port mismatch | remote client adapter | host / server 參數一致 | done |
| 修正 Akka config path 解析 | remote client adapter | 非輸出目錄啟動不再因 cwd 漂移失敗 | done |
| 更新 `Akka` / `Akka.Remote` 安全版本 | build / audit evidence | `NU1904` 消失 | done |
| 將 `FsiActor.fs` 實體移入 `FsiHost` | 前述 backend 收斂 | actor 路徑與 compile ownership 一致 | done |
| 加入 Windows service hosting 與 service name 參數 | actor 路徑調整 | `fsihost` / `fsharp-devkit` 可對應 SCM 服務名 | done |
| 建立 deployment script | service hosting | 可將兩套 artifact 複製到遠端並註冊服務 | done |
| 盤點並整理 `scripts/` placeholder | deployment script | demo 腳本不再偽裝成可用工具 | done |

## 測試

| 任務 | 驗收 | 狀態 |
|---|---|---|
| workaround build | 可看見真實 compile 狀態 | done |
| host + sync execute smoke test | 遠端 session 可用 | done |
| async enqueue + polling smoke test | `asyncId` lifecycle 正常 | done |
| package reference regression | `#r "nuget:..."` 可在同一 session 使用 | done |
| reset / restart regression | session state 可重建 | done |
| audit-enabled build | solution build 不再出現 `NU1904` | done |
| project vulnerability audit | `FsiHost` / `Server` 無 vulnerable packages | done |
| Release build / publish | `FsiHost` 與 `Server` 均可產出部署 artifact | done |
| deploy script syntax / WhatIf | 腳本可被 PowerShell 解析且 `-WhatIf` 成功 | done |

## 回饋

| 任務 | 驗收 | 狀態 |
|---|---|---|
| 更新 DevLog | 有本輪決策與驗證證據 | done |
| 更新 MCP.KM.md | 有 merge 知識沉澱 | done |
| 更新 Deployment / Runbook | 有部署步驟與回滾手冊 | done |
| 執行 check.fsx | 無與本輪目標衝突的 FAIL/WARN | done |
