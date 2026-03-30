# SA

## 2026-03-28 Async Status Tooling Gap

### Background

`fsharp-devkit` 已經提供 async status resource：

- `fsi/async/{asyncId}`

server 端也已實作 `resources/read`。但實際 agent 操作經驗顯示，較弱的 client / agent 常見兩種情況：

1. 沒有把 `resources/read` 暴露成易用工具
2. 知道有 resource，卻不會正確組純 HTTP JSON-RPC 的 `resources/read`

在這種情況下，agent 雖然能成功：

- 建立 host
- 建立 session
- 送出 `execute_f_sharp_code_async_routed`

但會卡在「拿到 `asyncId` 後不知道怎麼查狀態」。

### Problem

目前 async workflow 對強 client 是完整的，但對弱 client 不夠友善：

- async execute 的產物是 `asyncId`
- 狀態查詢只靠 resource surface
- 某些 agent 會因此誤判成 server 缺功能，或改走脆弱的 shell / heredoc workaround

### Goal

補一個與 resource 同語意、但更容易被 agent 直接使用的 MCP tool：

- `get_async_status(asyncId)`

使 agent 可以：

1. 呼叫 async execute
2. 拿到 `asyncId`
3. 用 tool 直接輪詢狀態
4. 完成後再 evaluate

### Non-Goals

- 不移除既有 `fsi/async/{asyncId}` resource
- 不改變 async queue / registry 的核心語意
- 不把 routed/default 分成兩套 async status tool

### Acceptance

1. `get_async_status` 與 `fsi/async/{asyncId}` 回傳同一份 `AsyncFsiStatusDto` 語意
2. tool 可用於 default async 與 routed async
3. client smoke 至少有一條案例完全不依賴 `resources/read`
