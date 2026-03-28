# Policy

## Scope

- 適用於 `FSharp.MCP.DevKit - Async` 本輪 mixed-runtime + async merge 任務。
- 適用檔案：
  - `doc/*.md`
  - `log/*.log`
  - `log/*.op_log`
  - `src/**/*.fs`
  - `src/**/*.fsproj`

## Naming Rule

- branch：`YYYYMMDD_001.topic`
- log：`YYYYMMDDHHmmss.<摘要>.<主編號>.<子編號>.log`
- op log：`YYYYMMDDHHmmss.<摘要>.op_log`
- 文件名稱固定使用：
  - `doc/SA.md`
  - `doc/SD.md`
  - `doc/WBS.md`
  - `doc/Test.md`
  - `doc/Policy.md`
  - `doc/Action.md`
  - `doc/DevLog.md`

## File Retention Strategy

- `doc/*.md` 一律納入 Git。
- `log/*.log` 與 `log/*.op_log` 一律納入 Git。
- `doc/DevLog.md` 採 append-only。
- 本輪不新增機密檔案，不建立新的 `nuget.config`。

## Risk And Exception

- `csharp-sdk` 的 `NU1903` 屬外部依賴 gate；本輪允許以 local build workaround 驗證本 repo compile，但不可把 workaround 當成最終 release 解法。
- 若 `check.fsx` 因缺少 external tracking 失敗，需回報使用者補齊 Jira / GitHub / Confluence。
- 若使用者在 target repo 有既有 dirty worktree，本輪不得覆蓋其未要求回滾的內容。

## Effective Date And Version

- Effective date: 2026-03-19
- Version: 0.2.1-async-merge
