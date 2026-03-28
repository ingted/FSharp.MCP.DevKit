# E2E Scenario Test

> 目標讀者：其他 LLM Agent
>
> 目標：在 deployed `fsharp-devkit` MCP server 上，建立一個 **remote / out-of-process** FSI host 與 session，執行指定 `.fsx` 的前 76 行，然後在同一個 session 取值：
>
> `cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c`

---

## 1. 成功條件

你完成此情境測試時，必須做到：

1. 建立一個新的 remote `net10` host。
2. 在該 host 上建立一個新的 session。
3. 將 `/workspace/home/work/coldfar-symbolics/experiments/generate_real_charts.inspect_930k_vs_30k.fsx` 的 **第 1~76 行** 送進該 session 執行。
4. 不修改原始 `.fsx` 檔，只改「送進 MCP 的字串內容」。
5. 執行完成後，在 **同一個 session** 取值：
   - `cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c`
6. 回報最終值，並說明是否成功。

---

## 2. 先理解路徑：不要直接送 agent 自己看到的絕對路徑

這是目前最容易失敗的地方。

### 2.1 目前部署拓樸

常見部署是：

1. host machine：`/home/sa/gemini4`
2. agent container：看到 `/workspace/home/...`
3. `fsharp-devkit` container：看到 `/gemini4/...` 與 `/workspace/...`

remote host / remote FSI session 是在 `fsharp-devkit` container 內執行，不是在 agent container 內執行。

### 2.2 這代表什麼

如果腳本中有：

```fsharp
#I @"/workspace/home/work/sharftrade7/..."
```

那個路徑對 agent container 也許存在，但對 remote host 不一定存在。

所以你**不能直接把原始前 76 行原封不動送進 remote session**。

---

## 3. 本案例一定要做的路徑改寫

目標檔案：

`/workspace/home/work/coldfar-symbolics/experiments/generate_real_charts.inspect_930k_vs_30k.fsx`

### 3.1 讀出第 1~76 行

先讀第 1~76 行，然後在記憶中或暫存字串中改寫，**不要改原始檔**。

### 3.2 必改項目

#### A. 拿掉 `#if INTERACTIVE` / `#endif`

原始檔開頭有：

```fsharp
#if INTERACTIVE
...
#endif
```

送進 remote session 前，直接拿掉這兩行包裝。

#### B. 改 `#I` 路徑

原始內容：

```fsharp
#I @"/workspace/home/work/sharftrade7/實驗/SharFTrade.Exp/bin/net10.0"
```

送入 remote host 前，改成：

```fsharp
#I @"/gemini4/work/sharftrade7/實驗/SharFTrade.Exp/bin/net10.0"
```

#### C. 讓 PCSL root 指向 remote container 真能看到的資料路徑

原始檔 `resolvePcslRoot()` 內會 fallback 到：

```fsharp
"/workspace/home/vhdx/cFar_pcsl2/cFar2"
"/workspace/home/vhdx/cFar_pcsl2"
```

在 remote container 內，通常應改成：

```fsharp
"/gemini4/vhdx/cFar_pcsl2/cFar2"
"/gemini4/vhdx/cFar_pcsl2"
```

最穩的做法是：在送進 remote session 的程式片段最前面，加一行：

```fsharp
System.Environment.SetEnvironmentVariable("SHARFTRADE_PCSL_ROOT", "/gemini4/vhdx/cFar_pcsl2/cFar2")
```

這樣 `resolvePcslRoot()` 會優先吃到正確值。

### 3.3 本案例的建議送入內容

組裝後，送入 remote session 的 code 應該長這樣：

1. 第一行先加：

```fsharp
System.Environment.SetEnvironmentVariable("SHARFTRADE_PCSL_ROOT", "/gemini4/vhdx/cFar_pcsl2/cFar2")
```

2. 再接上第 1~76 行，但已做：
   - 移除 `#if INTERACTIVE`
   - 移除 `#endif`
   - `#I "/workspace/home/..."` 改為 `#I "/gemini4/..."`
   - `"/workspace/home/vhdx/..."` 改為 `"/gemini4/vhdx/..."`

### 3.4 絕對不要做的事

1. 不要修改原始 `generate_real_charts.inspect_930k_vs_30k.fsx`
2. 不要把 agent container 的路徑原封不動送進 remote host
3. 不要先用 sync routed execute 跑這種長腳本

---

## 4. 推薦流程：MCP Tools 版本

### 4.1 為什麼先用 async routed execute

本案例屬於長腳本：

1. 有 `#I` / `#r`
2. 會載 DLL
3. 會初始化大量資料
4. 可能執行超過單次同步 ask 的穩定時間窗

因此本案例**優先**使用：

- `execute_f_sharp_code_async_routed`

完成後再用：

- `evaluate_f_sharp_expression_routed`

這兩者是 **同一個 host、同一個 session**，差別只在控制流程：

1. `execute_f_sharp_code_async_routed`
   - 先排進去
   - 拿到 `asyncId`
   - 輪詢完成
2. `evaluate_f_sharp_expression_routed`
   - 在同一 session 裡讀值

### 4.2 建議工具呼叫順序

1. `register_fsi_agent`
2. `create_fsi_host`
3. `create_fsi_session`
4. `execute_f_sharp_code_async_routed`
5. 輪詢 `fsi/async/{asyncId}`
6. `evaluate_f_sharp_expression_routed`

### 4.3 建議參數

#### `register_fsi_agent`

```json
{
  "agentId": "e2e-agent",
  "displayName": "E2E Agent"
}
```

#### `create_fsi_host`

`hostId` 必須唯一，且 `arguments` 裡的 `--procid` 必須與 `hostId` 一致。

```json
{
  "agentId": "e2e-agent",
  "hostId": "e2e-host-001",
  "hostKind": "net10",
  "executablePath": "dotnet",
  "arguments": "exec --runtimeconfig /app/Akka.Proc.Supervisor.runtimeconfig.json --depsfile /app/Akka.Proc.Supervisor.deps.json /app/Akka.Proc.Supervisor.dll --mode procnode --systemname fsi-proc --host 10.28.112.140 --port 0 --supervisor akka.tcp://proc-system@10.28.112.140:8110/user/proc-supervisor --procid e2e-host-001"
}
```

`probeMessage` / `probeIntervalMs` 可以省略；本案例不是必需。

#### `create_fsi_session`

```json
{
  "agentId": "e2e-agent",
  "hostId": "e2e-host-001",
  "sessionId": "e2e-session-001",
  "sessionName": "E2E Session"
}
```

#### `execute_f_sharp_code_async_routed`

```json
{
  "agentId": "e2e-agent",
  "hostId": "e2e-host-001",
  "sessionId": "e2e-session-001",
  "code": "<已改寫路徑與前置環境變數的第1~76行>"
}
```

#### `evaluate_f_sharp_expression_routed`

```json
{
  "agentId": "e2e-agent",
  "hostId": "e2e-host-001",
  "sessionId": "e2e-session-001",
  "expression": "cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c"
}
```

### 4.4 async 輪詢規則

拿到 `asyncId` 後，讀 resource：

`fsi/async/{asyncId}`

直到：

1. `isCompleted = true`
2. 或 `status = Completed`

若 `isSuccess = false` 或結果中有錯誤，就停止，不要繼續 evaluate。

### 4.5 成功判定

只有在以下都成立時才算成功：

1. async execute 完成且成功
2. evaluate 成功
3. 有拿到最終值

---

## 5. 純 HTTP Request 版本（當 MCP Tool 呼叫失敗時）

如果你所在的 agent 無法直接 tool-call MCP，也可以用純 HTTP 對 `fsharp-devkit` 完成同一件事。

MCP endpoint：

`http://10.28.112.140:15000/mcp`

### 5.1 先 initialize，拿 session id

```bash
curl -s http://10.28.112.140:15000/mcp \
  -X POST \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc":"2.0",
    "id":1,
    "method":"initialize",
    "params":{
      "protocolVersion":"2024-11-05",
      "capabilities":{},
      "clientInfo":{"name":"manual-http","version":"1.0"}
    }
  }'
```

回應 header 中會有：

`mcp-session-id: <SESSION_ID>`

### 5.2 送 initialized notification

```bash
curl -s http://10.28.112.140:15000/mcp \
  -X POST \
  -H "Content-Type: application/json" \
  -H "mcp-session-id: <SESSION_ID>" \
  -d '{
    "jsonrpc":"2.0",
    "method":"notifications/initialized",
    "params":{}
  }'
```

### 5.3 依序呼叫 tools/call

#### `register_fsi_agent`

```bash
curl -s http://10.28.112.140:15000/mcp \
  -X POST \
  -H "Content-Type: application/json" \
  -H "mcp-session-id: <SESSION_ID>" \
  -d '{
    "jsonrpc":"2.0",
    "id":2,
    "method":"tools/call",
    "params":{
      "name":"register_fsi_agent",
      "arguments":{
        "agentId":"e2e-agent",
        "displayName":"E2E Agent"
      }
    }
  }'
```

#### `create_fsi_host`

```bash
curl -s http://10.28.112.140:15000/mcp \
  -X POST \
  -H "Content-Type: application/json" \
  -H "mcp-session-id: <SESSION_ID>" \
  -d '{
    "jsonrpc":"2.0",
    "id":3,
    "method":"tools/call",
    "params":{
      "name":"create_fsi_host",
      "arguments":{
        "agentId":"e2e-agent",
        "hostId":"e2e-host-001",
        "hostKind":"net10",
        "executablePath":"dotnet",
        "arguments":"exec --runtimeconfig /app/Akka.Proc.Supervisor.runtimeconfig.json --depsfile /app/Akka.Proc.Supervisor.deps.json /app/Akka.Proc.Supervisor.dll --mode procnode --systemname fsi-proc --host 10.28.112.140 --port 0 --supervisor akka.tcp://proc-system@10.28.112.140:8110/user/proc-supervisor --procid e2e-host-001"
      }
    }
  }'
```

#### `create_fsi_session`

```bash
curl -s http://10.28.112.140:15000/mcp \
  -X POST \
  -H "Content-Type: application/json" \
  -H "mcp-session-id: <SESSION_ID>" \
  -d '{
    "jsonrpc":"2.0",
    "id":4,
    "method":"tools/call",
    "params":{
      "name":"create_fsi_session",
      "arguments":{
        "agentId":"e2e-agent",
        "hostId":"e2e-host-001",
        "sessionId":"e2e-session-001",
        "sessionName":"E2E Session"
      }
    }
  }'
```

#### `execute_f_sharp_code_async_routed`

把 `<CODE_JSON_STRING>` 換成你已改寫好的程式字串。

```bash
curl -s http://10.28.112.140:15000/mcp \
  -X POST \
  -H "Content-Type: application/json" \
  -H "mcp-session-id: <SESSION_ID>" \
  -d '{
    "jsonrpc":"2.0",
    "id":5,
    "method":"tools/call",
    "params":{
      "name":"execute_f_sharp_code_async_routed",
      "arguments":{
        "agentId":"e2e-agent",
        "hostId":"e2e-host-001",
        "sessionId":"e2e-session-001",
        "code":"<CODE_JSON_STRING>"
      }
    }
  }'
```

### 5.4 輪詢 async resource

MCP resource read 請求：

```bash
curl -s http://10.28.112.140:15000/mcp \
  -X POST \
  -H "Content-Type: application/json" \
  -H "mcp-session-id: <SESSION_ID>" \
  -d '{
    "jsonrpc":"2.0",
    "id":6,
    "method":"resources/read",
    "params":{
      "uri":"fsi/async/<ASYNC_ID>"
    }
  }'
```

直到 `isCompleted=true`。

### 5.5 evaluate 最終值

```bash
curl -s http://10.28.112.140:15000/mcp \
  -X POST \
  -H "Content-Type: application/json" \
  -H "mcp-session-id: <SESSION_ID>" \
  -d '{
    "jsonrpc":"2.0",
    "id":7,
    "method":"tools/call",
    "params":{
      "name":"evaluate_f_sharp_expression_routed",
      "arguments":{
        "agentId":"e2e-agent",
        "hostId":"e2e-host-001",
        "sessionId":"e2e-session-001",
        "expression":"cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c"
      }
    }
  }'
```

---

## 6. 常見失敗與判斷

### 6.1 `DynamicObj.dll` 或其他 DLL 找不到

這通常不是 host/session 壞掉，而是你沒把：

`/workspace/home/...`

改成 remote host 真能看到的：

`/gemini4/...`

### 6.2 `create_fsi_host` 成功，但腳本跑不起來

先檢查：

1. `#I` 是否改到 remote 可見路徑
2. `SHARFTRADE_PCSL_ROOT` 是否先設成 `/gemini4/vhdx/cFar_pcsl2/cFar2`
3. 你是不是用了 sync execute 跑長腳本

### 6.3 sync execute 失敗，但 async execute 成功

這不代表 session 語意不同。通常只代表你踩到同步長等待路徑的穩定性問題。

做法：

1. 改用 `execute_f_sharp_code_async_routed`
2. async 完成後在同一 session evaluate

### 6.4 不要直接用舊的 host/session id

每次測試請盡量使用 fresh id，例如：

1. `e2e-host-20260328-01`
2. `e2e-session-20260328-01`

避免拿 stale host/session 的狀態誤判成這一輪的新問題。

---

## 7. 最短操作摘要

如果你只想記住最少規則，請記這 6 點：

1. 先建立 fresh `agentId/hostId/sessionId`
2. `create_fsi_host` 用 `net10` + `/app/Akka.Proc.Supervisor...`
3. 讀目標 `.fsx` 第 1~76 行，但只改「送出的字串」，不要改原檔
4. 把 `#I /workspace/home/...` 改成 `/gemini4/...`
5. 先設 `SHARFTRADE_PCSL_ROOT=/gemini4/vhdx/cFar_pcsl2/cFar2`
6. 長腳本一律先 `execute_f_sharp_code_async_routed`，完成後再 `evaluate_f_sharp_expression_routed`
