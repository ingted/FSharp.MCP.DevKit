# E2E Scenario Test For Gemini CLI

> 目標讀者：使用 Gemini CLI 的較弱 agent
>
> 目標：在 deployed `fsharp-devkit` MCP server 上建立一個 fresh remote `net10` host 與 session，執行指定 `.fsx` 的第 1~76 行，然後在同一個 session 取值：
>
> `cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c`
>
> 注意：本文中的 host 路徑、container 路徑、IP、mount point 都是某一個可工作的部署範例，不是通用固定值。實際使用時，請替換成你自己的環境值。

---

## 1. 這份文件和一般版有什麼不同

這份文件是給 **Gemini CLI** 用的。

Gemini CLI 在某些環境下常見的限制是：

1. 只有本地 `read_file`
2. 有 MCP server tool surface
3. **沒有**穩定可用的 `write_file`
4. **沒有**穩定可用的 shell / heredoc / 本地腳本落地能力
5. 可能會亂用 `generalist` 或類似 delegation 工具

因此這份流程的核心原則是：

1. **只用本地 `read_file` + `fsharp-devkit` MCP tools**
2. **不要依賴本地暫存檔**
3. **不要依賴 shell**
4. **不要委派，不要用 `generalist`**
5. 直接把 transformed F# code 當字串送進 MCP

### 1.1 Gemini CLI 的 MCP naming 與設定位置

Gemini CLI 的 MCP 設定檔是：

- `~/.gemini/settings.json`

不是：

- `~/.gemini/mcp_servers.json`

對 `fsharp-devkit` 這類 MCP server，Gemini CLI 內部真正註冊給模型的工具名稱是 fully-qualified name：

- `mcp_fsharp-devkit_register_fsi_agent`
- `mcp_fsharp-devkit_create_fsi_host`
- `mcp_fsharp-devkit_create_fsi_session`
- `mcp_fsharp-devkit_get_lines`
- `mcp_fsharp-devkit_execute_f_sharp_code_async_routed`
- `mcp_fsharp-devkit_get_async_status`
- `mcp_fsharp-devkit_evaluate_f_sharp_expression_routed`

如果 prompt 需要明確指定工具，請使用這種 `mcp_{server}_{tool}` 名稱，不要自行發明：

- `fsharp_devkit.register_fsi_agent`
- `fsharp-devkit.register_fsi_agent`

### 1.2 Headless mode 的提示原則

Gemini CLI 的 headless mode 常見失敗模式是：

1. 讀完文件後停住
2. 亂選工具
3. 轉去 `generalist`
4. 不直接使用已載入的 MCP tool

因此 prompt 應儘量明確要求：

1. 只使用 `fsharp-devkit` MCP tools
2. 不要 delegation / `generalist`
3. 必要時直接點名 fully-qualified MCP tool name

---

## 2. 成功條件

你完成此情境測試時，必須做到：

1. 建立 fresh remote `net10` host
2. 建立 fresh session
3. 執行目標 `.fsx` 的第 1~76 行
4. **不修改原始檔**
5. 只改「送進 MCP 的 code string」
6. 回傳：
   - host / session 建立成功
   - async execute 成功完成
   - `cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c` 的值

---

## 3. 路徑規則

### 3.1 目前部署拓樸

目前常見部署是：

1. host machine
   - `/home/sa/gemini4`
   - `/home/sa/gemini4/devkit_workspace`
2. `fsharp-devkit` container
   - `-v /home/sa/gemini4:/gemini4:ro`
   - `-v /home/sa/gemini4/devkit_workspace:/workspace`

所以 remote host 真正看到的是：

- `/gemini4/...` 對應 host `/home/sa/gemini4/...`
  - **唯讀**
  - 用來讀 source tree / DLL / `.fsx`
- `/workspace/...` 對應 host `/home/sa/gemini4/devkit_workspace/...`
  - **可讀寫**
  - 只有在 remote host 真的需要輸出檔案時才用

### 3.2 對 Gemini 的要求

Gemini 自己看到的本地絕對路徑，不一定等於 remote host 可見路徑。

因此：

1. 讀本文件等 workspace 內檔案，可以用本地 `read_file`
2. 讀 workspace 外的原始 `.fsx`，**不要**用本地 `read_file`
3. 對這種外部檔案，改用 `mcp_fsharp-devkit_get_lines`
4. 送進 remote host 的字串中，路徑要改成 remote host 真能看到的路徑
5. 對本案例，重點是把 `/workspace/home/...` 改成 `/gemini4/...`

---

## 4. 本案例一定要做的字串改寫

目標檔案：

`/workspace/home/work/coldfar-symbolics/experiments/generate_real_charts.inspect_930k_vs_30k.fsx`

Gemini 不應直接用本地 `read_file` 讀這個路徑，因為它常常不在 Gemini 當前 workspace 允許範圍內。

請改用：

```json
{
  "filePath": "/gemini4/work/coldfar-symbolics/experiments/generate_real_charts.inspect_930k_vs_30k.fsx",
  "startLine": 1,
  "endLine": 76
}
```

也就是：
- 用 `mcp_fsharp-devkit_get_lines`
- 路徑直接改成 `fsharp-devkit` container 內可見的 `/gemini4/...`

然後只改送進 MCP 的字串。

### 4.1 拿掉 `#if INTERACTIVE` / `#endif`

原始檔開頭若有：

```fsharp
#if INTERACTIVE
...
#endif
```

送進 remote session 前，拿掉這兩行包裝。

### 4.2 改 `#I` 路徑

例如原始字串若有：

```fsharp
#I @"/workspace/home/work/sharftrade7/實驗/SharFTrade.Exp/bin/net10.0"
```

送進 remote host 前改成：

```fsharp
#I @"/gemini4/work/sharftrade7/實驗/SharFTrade.Exp/bin/net10.0"
```

### 4.3 先設 PCSL root

在送進 remote session 的 code 最前面加：

```fsharp
System.Environment.SetEnvironmentVariable("SHARFTRADE_PCSL_ROOT", "/gemini4/vhdx/cFar_pcsl2/cFar2")
```

若原始前 76 行裡有：

```fsharp
"/workspace/home/vhdx/cFar_pcsl2/cFar2"
"/workspace/home/vhdx/cFar_pcsl2"
```

也改成：

```fsharp
"/gemini4/vhdx/cFar_pcsl2/cFar2"
"/gemini4/vhdx/cFar_pcsl2"
```

### 4.4 不要做的事

1. 不要改原始 `.fsx`
2. 不要把 agent 本地路徑原封不動送進 remote host
3. 不要先走 sync routed execute
4. 不要先把 code 寫到本地 temp file，除非你真的確定 Gemini 當前環境有 `write_file`

---

## 5. Gemini CLI 的推薦操作方式

### 5.1 只走 MCP tools，不走本地 shell

Gemini CLI 版本的主流程是：

1. 本地 `read_file` 只讀本文件或 workspace 內說明檔
2. `mcp_fsharp-devkit_get_lines` 讀目標 `.fsx` 的第 1~76 行
3. 在模型記憶體中完成字串改寫
4. `mcp_fsharp-devkit_register_fsi_agent`
5. `mcp_fsharp-devkit_create_fsi_host`
6. `mcp_fsharp-devkit_create_fsi_session`
7. `mcp_fsharp-devkit_execute_f_sharp_code_async_routed`
8. `mcp_fsharp-devkit_get_async_status`
9. 完成後 `mcp_fsharp-devkit_evaluate_f_sharp_expression_routed`

### 5.1A 長腳本請拆成兩階段

對 `gemini-3.1-pro-preview` headless 來說，長腳本最穩的模式不是在單一回合裡：

- create host
- create session
- async execute
- repeated polling
- evaluate

而是拆成兩個階段：

1. 第一回合：
   - 建 host / session
   - 讀外部 `.fsx`
   - 做 path rewrite
   - `mcp_fsharp-devkit_execute_f_sharp_code_async_routed`
   - 最多只做一次 `mcp_fsharp-devkit_get_async_status`
2. 後續回合：
   - 每回合只做一次 `mcp_fsharp-devkit_get_async_status`
   - 只有在 `isCompleted=true` 時才 `evaluate`

原因：

- Gemini 3.1 在同一回合裡做 repeated same-tool polling，容易觸發自己的 loop recovery
- 如果 async job 本身需要較長時間，反而會讓 agent 在 prompt 層被誤判成「不會用工具」

因此：

1. 不要在同一回合要求它輪詢 5~10 次
2. 多回合單次查詢比較穩
3. 如果多次單次查詢都仍是 `Running`，優先懷疑業務腳本耗時，不要先怪 prompt

### 5.2 為什麼不用本地 temp file

因為 Gemini CLI 在某些環境裡：

1. 沒有 `write_file`
2. 沒有可用 shell
3. 會在 heredoc / quoting / 本地工具缺失上浪費時間

而這個案例其實**不需要**本地 temp file。

你完全可以：

1. 用 `mcp_fsharp-devkit_get_lines` 讀出前 76 行
2. 在腦中或模型內完成 path rewrite
3. 把最終 F# code 字串直接送給 `execute_f_sharp_code_async_routed`

### 5.3 一律禁止 delegation

Gemini CLI 在某些情況會亂轉去：

- `generalist`
- 類似 subagent / delegation 工具

本案例禁止這樣做。

你要：

1. 自己完成
2. 只使用：
   - 本地 `read_file`
   - `fsharp-devkit` MCP tools

---

## 6. MCP tools 順序

### 6.1 `register_fsi_agent`

範例：

```json
{
  "agentId": "gemini-e2e-agent-20260329-01",
  "displayName": "Gemini E2E Agent"
}
```

### 6.2 `create_fsi_host`

範例：

```json
{
  "agentId": "gemini-e2e-agent-20260329-01",
  "hostId": "gemini-e2e-host-20260329-01",
  "hostKind": "net10",
  "executablePath": "dotnet",
  "arguments": "exec --runtimeconfig /app/Akka.Proc.Supervisor.runtimeconfig.json --depsfile /app/Akka.Proc.Supervisor.deps.json /app/Akka.Proc.Supervisor.dll --mode procnode --systemname fsi-proc --host 10.28.112.140 --port 0 --supervisor akka.tcp://proc-system@10.28.112.140:8110/user/proc-supervisor --procid gemini-e2e-host-20260329-01"
}
```

說明：

1. `hostId` 要 fresh
2. `--procid` 要和 `hostId` 一致
3. `probeMessage` / `probeIntervalMs` 可省略

### 6.3 `create_fsi_session`

```json
{
  "agentId": "gemini-e2e-agent-20260329-01",
  "hostId": "gemini-e2e-host-20260329-01",
  "sessionId": "gemini-e2e-session-20260329-01",
  "sessionName": "Gemini E2E Session"
}
```

### 6.4 `execute_f_sharp_code_async_routed`

把 transformed code string 直接送進去：

```json
{
  "agentId": "gemini-e2e-agent-20260329-01",
  "hostId": "gemini-e2e-host-20260329-01",
  "sessionId": "gemini-e2e-session-20260329-01",
  "code": "<transformed F# code string>"
}
```

### 6.5 `get_async_status`

拿到 `asyncId` 後，優先輪詢：

```json
{
  "asyncId": "<asyncId>"
}
```

若回：

- `exists = true`
- `status = "Running"`
- `isCompleted = false`

代表：

1. async job 存在
2. 還在跑
3. 不是失敗

### 6.6 `evaluate_f_sharp_expression_routed`

等 async 完成後，對同一個 host/session 取值：

```json
{
  "agentId": "gemini-e2e-agent-20260329-01",
  "hostId": "gemini-e2e-host-20260329-01",
  "sessionId": "gemini-e2e-session-20260329-01",
  "expression": "cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c"
}
```

---

## 7. 若 MCP tool call 失敗，Gemini 版怎麼處理

### 7.1 先分辨是哪一種失敗

如果失敗是：

1. `create_fsi_host` / `create_fsi_session` generic error
2. `execute_f_sharp_code_async_routed` generic error
3. `get_async_status` generic error

先不要立刻轉去 shell / temp file。

### 7.2 Gemini 版 fallback 原則

若你**沒有**這些能力：

1. `write_file`
2. shell / bash
3. 安全的本地 HTTP request 工具

那就：

1. 停下來
2. 回報：
   - 哪一個 MCP tool 失敗
   - tool 名稱
   - 錯誤字串
   - 你已遵守本文件只用 MCP tools 的流程

不要假裝你能做本地 HTTP fallback。

### 7.3 只有在你真的有本地寫檔與 shell 時，才參考一般版

若你確定自己當前環境真的有：

1. `write_file`
2. shell
3. Python

才去看：

- [E2EScenarioTest.md](/workspace/home/mcp/FSharp.MCP.DevKit/doc/E2EScenarioTest.md)

的純 HTTP fallback。

---

## 8. 最短操作摘要

只記這 8 點：

1. 先讀一般版路徑原則，但 Gemini 自己不要假設有本地寫檔
2. 本地 `read_file` 只讀本文件
3. 用 `mcp_fsharp-devkit_get_lines(/gemini4/... , 1, 76)` 讀目標 `.fsx`
4. 在記憶中完成 path rewrite，不改原始檔
5. fresh `agentId/hostId/sessionId`
6. `execute_f_sharp_code_async_routed`
7. `get_async_status`
8. 完成後 `evaluate_f_sharp_expression_routed`

---

## 9. Gemini 專用單次 Prompt 範本

```text
Read /workspace/home/mcp/FSharp.MCP.DevKit/doc/E2EScenarioTest_gemini.md first and follow it exactly.

Do the task yourself.
Do not use generalist, delegation, or any subagent-style tool.

Task:
1. Create a fresh remote net10 host on the deployed fsharp-devkit MCP server.
2. Create a fresh session on that host.
3. Use `mcp_fsharp-devkit_get_lines` to read lines 1~76 of `/gemini4/work/coldfar-symbolics/experiments/generate_real_charts.inspect_930k_vs_30k.fsx`.
4. Transform only the code string you send to MCP:
   - add SHARFTRADE_PCSL_ROOT for /gemini4/vhdx/cFar_pcsl2/cFar2
   - rewrite /workspace/home/... paths to /gemini4/... where required
   - remove #if INTERACTIVE / #endif
5. Execute the transformed code via execute_f_sharp_code_async_routed.
6. Poll get_async_status until completed.
7. Evaluate cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c.

Important:
- Do not modify the original fsx file.
- Do not assume you have write_file or shell.
- Use local `read_file` only for files inside the current workspace, such as this document.
- For the target `.fsx`, use `mcp_fsharp-devkit_get_lines` with the remote-visible `/gemini4/...` path.
- If an MCP tool fails generically and you do not have local write/shell tools, stop and report the exact blocker instead of inventing a fallback.
- When naming MCP tools explicitly, use Gemini CLI FQNs:
  - `mcp_fsharp-devkit_register_fsi_agent`
  - `mcp_fsharp-devkit_create_fsi_host`
  - `mcp_fsharp-devkit_create_fsi_session`
  - `mcp_fsharp-devkit_get_lines`
  - `mcp_fsharp-devkit_execute_f_sharp_code_async_routed`
  - `mcp_fsharp-devkit_get_async_status`
  - `mcp_fsharp-devkit_evaluate_f_sharp_expression_routed`
```
