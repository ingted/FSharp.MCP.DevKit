# WBS

## 里程碑

| ID | 項目 | 產出 | 狀態 |
|---|---|---|---|
| M1 | 完成 SA/SD/WBS/Test/Policy/Action/DevLog | `doc/*.md` | done |
| M2 | 收斂 mixed-runtime backend | server / core / host / messages code | done |
| M3 | 完成 build / smoke test / check | build log / test log / check result | done |

## Schedule

| ID | 時間 | 工作 | 驗收 |
|---|---|---|---|
| W01 | 2026-03-19 AM | 建立基線、review 三版結構與 build 現況 | 有 SA 問題列表與 build baseline |
| W02 | 2026-03-19 AM | 寫 SA / SD / WBS / Test / Policy / Action / DevLog | 文件可作為實作依據 |
| W03 | 2026-03-19 PM | 修正 message / actor / core target framework compile path | `FsiHost` 與 server 共享 transport contract |
| W04 | 2026-03-19 PM | 導入 remote client adapter，移除 server 內本地 FSI backend 依賴 | sync / async tools 共用單一 backend |
| W05 | 2026-03-19 PM | 修正 async queue 型別與 polling 路徑 | `execute_f_sharp_code_async` 能回傳 `asyncId` 並查狀態 |
| W06 | 2026-03-19 PM | 執行 build / smoke test / check | 有 `.op_log`、`DevLog`、`check.fsx` 結果 |

## Work Items

| ID | 工作 | 依賴 | 驗收 | 狀態 |
|---|---|---|---|---|
| T01 | 記錄 baseline build 與 review 結果 | 無 | `log/*.log`、`log/*.op_log` | done |
| T02 | 重寫 `doc/SA.md` | T01 | 問題、根因、scope 清楚 | done |
| T03 | 重寫 `doc/SD.md` | T02 | backend / data flow / rollback 清楚 | done |
| T04 | 重寫 `doc/WBS.md` | T02-T03 | 任務可執行 | done |
| T05 | 建立 `doc/Test.md` | T02-T03 | 正常/異常/回歸案例齊備 | done |
| T06 | 建立 `doc/Policy.md` | T02-T04 | scope / retention / 例外條件清楚 | done |
| T07 | 建立 `doc/Action.md` | T04 | phase / status 可追蹤 | done |
| T08 | 建立 `doc/DevLog.md` 初始條目 | T01-T04 | 有本輪任務紀錄 | done |
| T09 | 擴充 `Messages` transport DTO | T03 | actor 可接收統一 command | done |
| T10 | 修正 `Core.fsproj` target framework 與 `FsiActor` 編譯條件 | T03 | host compile path 恢復 | done |
| T11 | 更新 `FsiActor.fs` 支援 remote command dispatch | T09-T10 | host 能處理 sync / async 同一套命令 | done |
| T12 | 更新 `McpFsiTools.fs` 的 remote client adapter | T09-T11 | 不再依賴 server 本地 FSI session | done |
| T13 | 修正 async queue 型別與 cache 更新流程 | T12 | queue worker 可執行與回填結果 | done |
| T14 | 修正 Akka port 與 host / server 設定一致性 | T10-T12 | server 可連到 host | done |
| T15 | local build workaround 下執行 compile 驗證 | T09-T14 | 真實 compile error 收斂 | done |
| T16 | 執行 smoke test 與 `check.fsx` | T15 | 無與本輪目標衝突的 FAIL/WARN | done |

## 完成定義

- `FSharp.MCP.DevKit - Async` 的 sync / async FSI tools 不再混用 local FSI 與 remote FSI 兩條 backend。
- net472 host 與 .NET 9/10 server 的責任邊界恢復清楚。
- async queue / polling 可在單一遠端 session 上工作。
- 文件、log、DevLog、check 皆可追溯。
