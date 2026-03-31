# E2E Scenario Test

> 目標讀者：其他 LLM Agent
>
> 目標：在 deployed `fsharp-devkit` MCP server 上，建立一個 **remote / out-of-process** FSI host 與 session，執行指定 `.fsx` 的前 76 行，然後在同一個 session 取值：
>
> `cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c`
>
> 注意：本文中的 host 路徑、container 路徑、IP、mount point 都是某一個可工作的部署範例，不是通用固定值。實際使用時，請替換成你自己的環境值。

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

1. host machine：
   - source tree：`/home/sa/gemini4`
   - writable workspace：`/home/sa/gemini4/devkit_workspace`
2. agent container：
   - 看到 `/workspace/home/...`
3. `fsharp-devkit` container：
   - `-v /home/sa/gemini4:/gemini4:ro`
   - `-v /home/sa/gemini4/devkit_workspace:/workspace`

remote host / remote FSI session 是在 `fsharp-devkit` container 內執行，不是在 agent container 內執行。

上面的 `/home/sa/gemini4`、`/gemini4`、`/workspace` 都只是範例。你必須根據自己的 host 掛載方式替換成對應路徑。

更精確地說：

| 視角 | container/path | 對應 host 路徑 | 權限 | 典型用途 |
|------|----------------|-----------------|------|----------|
| agent container | `/workspace/home/...` | `/home/sa/gemini4/...` | 取決於 agent container 掛載方式 | agent 本地讀檔、產生 prompt、查看 repo |
| `fsharp-devkit` container | `/gemini4/...` | `/home/sa/gemini4/...` | **唯讀** | remote host 讀 source tree、讀非 NuGet DLL、讀腳本 |
| `fsharp-devkit` container | `/workspace/...` | `/home/sa/gemini4/devkit_workspace/...` | **可讀寫** | remote host / server 寫暫存輸出、工作檔、可寫入資料 |

因此：

1. **要讀 source tree / DLL / 腳本時，優先考慮 `/gemini4/...`**
2. **要寫暫存檔或中間產物時，應該用 `/workspace/...`**
3. 不要把 `/workspace/home/...` 當成 remote host 一定能看到的路徑

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

補充：

1. 本案例是重 workload，`status = Running` 持續數十秒以上不代表失敗
2. 只要 `exists = true` 且 `isCompleted = false`，就表示 job 仍在 server 端正常存在
3. 不要因為 30~60 秒內尚未完成就判定 server 壞掉
4. 對這個案例，建議至少給 5~10 分鐘的輪詢預算

### 4.5 成功判定

只有在以下都成立時才算成功：

1. async execute 完成且成功
2. evaluate 成功
3. 有拿到最終值

---

## 5. 純 HTTP Request 版本（當 MCP Tool 呼叫失敗時）

如果你所在的 agent 無法直接 tool-call MCP，也可以用純 HTTP 對 `fsharp-devkit` 完成同一件事。

### 5.0 先釐清：不是 server 缺 `resources/read`

`fsharp-devkit` server 已經有：

1. resource template：`fsi/async/{asyncId}`
2. MCP `resources/read`

所以這裡通常不是 server 功能缺失，而是：

1. 某些 client 沒把 `resources/read` 暴露成好用的 agent 能力
2. 或 agent 不知道怎麼直接呼叫 MCP resource API

因此，本節提供的是 **client/agent tool surface 不夠完整時** 的 fallback。

MCP endpoint：

`http://10.28.112.140:15000/mcp`

### 5.1 不要用 `curl + inline 多行 F# code`

對弱一點的 agent，最容易先壞掉的是：

1. 多行 F# 片段
2. shell heredoc
3. JSON escaping
4. `curl -d '...'`

這四件事混在一起，很容易在真正送出 MCP request 前就先壞在 shell parsing。

因此：

- **不建議**把整段多行 F# code 直接 inline 到 `curl`
- **建議**先把 transformed code 寫到暫存檔，再用 Python 讀檔送 JSON-RPC

### 5.2 推薦做法：單一 Python 腳本完成整個 HTTP workflow

#### Step A：先把 transformed code 寫到暫存檔

先把「依第 3 節規則改寫過的 code」寫到：

`/workspace/home/work/.gemini-tmp/e2e_scenario_code.fsx`

不要寫到 `/tmp/...`，因為某些 agent CLI 的本地 workspace sandbox 不允許：

1. 任意讀寫 `/tmp`
2. 在 workspace 外建立暫存檔

如果你是用 Gemini CLI，請優先使用：

`--include-directories /workspace/home/work`

並把 transformed code 寫到該 workspace 內，例如：

`/workspace/home/work/.gemini-tmp/e2e_scenario_code.fsx`

內容應包含：

1. 第一行：

```fsharp
System.Environment.SetEnvironmentVariable("SHARFTRADE_PCSL_ROOT", "/gemini4/vhdx/cFar_pcsl2/cFar2")
```

2. 接著是改寫後的第 1~76 行：
   - 移除 `#if INTERACTIVE`
   - 移除 `#endif`
   - `#I "/workspace/home/..."` 改成 `#I "/gemini4/..."`
   - `"/workspace/home/vhdx/..."` 改成 `"/gemini4/vhdx/..."`

#### Step B：用 Python 完成 initialize / tools/call / resources/read / evaluate

```python
import json
import pathlib
import time
import urllib.request

ENDPOINT = "http://10.28.112.140:15000/mcp"
AGENT_ID = "e2e-agent"
HOST_ID = "e2e-host-001"
SESSION_ID = "e2e-session-001"
CODE_PATH = "/workspace/home/work/.gemini-tmp/e2e_scenario_code.fsx"

HOST_ARGUMENTS = (
    "exec --runtimeconfig /app/Akka.Proc.Supervisor.runtimeconfig.json "
    "--depsfile /app/Akka.Proc.Supervisor.deps.json "
    "/app/Akka.Proc.Supervisor.dll "
    "--mode procnode "
    "--systemname fsi-proc "
    "--host 10.28.112.140 "
    "--port 0 "
    "--supervisor akka.tcp://proc-system@10.28.112.140:8110/user/proc-supervisor "
    f"--procid {HOST_ID}"
)

# 不要自行發明 create_fsi_host 的 net10 arguments。
# 若你使用 MCP tool，請優先直接複製這段 HOST_ARGUMENTS；
# 若 tool 仍報 generic error，再退回 HTTP JSON-RPC fallback。

EXPRESSION = (
    "cfar.Cfarta.[int scale].[set [Scale scale; USING 7; "
    "MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c"
)


def post(payload, session_id=None):
    req = urllib.request.Request(
        ENDPOINT,
        data=json.dumps(payload).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            **({"mcp-session-id": session_id} if session_id else {})
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=120) as resp:
        return resp.headers, resp.read().decode("utf-8")


def post_jsonrpc(payload, session_id=None):
    headers, body = post(payload, session_id=session_id)
    lines = [line for line in body.splitlines() if line.startswith("data: ")]
    if not lines:
        raise RuntimeError(f"No MCP data lines in response: {body}")
    return headers, json.loads(lines[-1][6:])


def initialize():
    headers, data = post_jsonrpc(
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "manual-http", "version": "1.0"},
            },
        }
    )
    session_id = (
        headers.get("mcp-session-id")
        or headers.get("Mcp-Session-Id")
        or headers.get("MCP-SESSION-ID")
    )
    if not session_id:
        raise RuntimeError("Missing mcp-session-id in initialize response")
    post(
        {
            "jsonrpc": "2.0",
            "method": "notifications/initialized",
            "params": {},
        },
        session_id=session_id,
    )
    return session_id

# NOTE:
# `notifications/initialized` must be sent as a notification, not a request.
# Do not include an `id` field. With Streamable HTTP the expected response is
# HTTP 202 with an empty body.


def call_tool(session_id, request_id, name, arguments):
    _, data = post_jsonrpc(
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": "tools/call",
            "params": {"name": name, "arguments": arguments},
        },
        session_id=session_id,
    )
    return data


def call_tool_text(session_id, request_id, name, arguments):
    data = call_tool(session_id, request_id, name, arguments)
    return extract_text(data)


def read_resource(session_id, request_id, uri):
    _, data = post_jsonrpc(
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": "resources/read",
            "params": {"uri": uri},
        },
        session_id=session_id,
    )
    return data


def extract_text(result):
    content = result["result"]["content"]
    text_items = [item["text"] for item in content if item.get("type") == "text"]
    if not text_items:
        raise RuntimeError(f"No text content in result: {result}")
    return text_items[0]


def wait_async_done(session_id, async_id):
    for i in range(180):
        try:
            payload = json.loads(
                call_tool_text(
                    session_id,
                    1000 + i,
                    "get_async_status",
                    {"asyncId": async_id}
                )
            )
        except Exception:
            data = read_resource(session_id, 1000 + i, f"fsi/async/{async_id}")
            contents = data["result"]["contents"]
            if not contents:
                raise RuntimeError(f"Missing resource contents: {data}")
            payload = json.loads(contents[0]["text"])
        if payload.get("exists") is False:
            raise RuntimeError(
                f"Async status is not visible yet or asyncId is wrong: {json.dumps(payload, ensure_ascii=False)}"
            )
        if payload.get("isCompleted") is True:
            return payload
        time.sleep(2)
    raise TimeoutError(f"Async job did not complete in time: {async_id}")


session_id = initialize()
code = pathlib.Path(CODE_PATH).read_text(encoding="utf-8")

call_tool(session_id, 2, "register_fsi_agent", {
    "agentId": AGENT_ID,
    "displayName": "E2E Agent"
})

call_tool(session_id, 3, "create_fsi_host", {
    "agentId": AGENT_ID,
    "hostId": HOST_ID,
    "hostKind": "net10",
    "executablePath": "dotnet",
    "arguments": HOST_ARGUMENTS
})

call_tool(session_id, 4, "create_fsi_session", {
    "agentId": AGENT_ID,
    "hostId": HOST_ID,
    "sessionId": SESSION_ID,
    "sessionName": "E2E Session"
})

async_resp = call_tool(session_id, 5, "execute_f_sharp_code_async_routed", {
    "agentId": AGENT_ID,
    "hostId": HOST_ID,
    "sessionId": SESSION_ID,
    "code": code
})

async_id = extract_text(async_resp).strip()
status_payload = wait_async_done(session_id, async_id)

if not status_payload.get("isSuccess", False):
    raise RuntimeError(f"Async execution failed: {json.dumps(status_payload, ensure_ascii=False)}")

eval_resp = call_tool(session_id, 6, "evaluate_f_sharp_expression_routed", {
    "agentId": AGENT_ID,
    "hostId": HOST_ID,
    "sessionId": SESSION_ID,
    "expression": EXPRESSION
})

print(json.dumps({
    "session_id": session_id,
    "async_id": async_id,
    "async_status": status_payload,
    "evaluate_response": eval_resp
}, ensure_ascii=False, indent=2))
```

### 5.3 initialize / session id 規則

你應理解上面 Python 做了什麼：

1. `initialize`
2. 從 response header 拿 `mcp-session-id`
3. 再送 `notifications/initialized`
4. 後續所有：
   - `tools/call`
   - `resources/read`
   都帶同一個 `mcp-session-id`

補充：

1. 某些 HTTP client 會把 response header 正規化成 `Mcp-Session-Id`
2. 如果你先把 headers 轉成普通 dict，再只抓小寫 `mcp-session-id`，會誤判成 server 沒給 session id
3. 所以應以 case-insensitive 方式讀取 session header

### 5.4 如果你仍想用 curl

可以，但請遵守：

1. 不要直接把多行 F# code inline 到 `curl -d '...'`
2. 至少先把 code 寫到檔案
3. 再用 `python -c` 或 `jq -Rs` 讀檔做 JSON escaping

否則你大概率會先壞在 shell parsing，而不是壞在 MCP server。

### 5.5 `execute_f_sharp_code_async_routed` 的回傳格式

這個 tool 的文字內容回傳的是：

- **純 `asyncId` 字串**

不是：

- `{\"asyncId\":\"...\"}` 這種 JSON 物件

所以 Python/HTTP fallback 應該這樣取值：

```python
async_id = extract_text(async_resp).strip()
```

不要這樣寫：

```python
async_payload = json.loads(extract_text(async_resp))
async_id = async_payload["asyncId"]
```

### 5.6 輪詢優先順序

若部署版本已有：

- `get_async_status`

則 Python/HTTP fallback 應優先：

1. `tools/call get_async_status(asyncId)`
2. 只有在 tool 不可用時，才退回 `resources/read fsi/async/{asyncId}`

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

### 6.4 tool surface 看不到 `resources/read`

這通常不是 `fsharp-devkit` server 少了功能，而是 client/agent 沒把 resource API 暴露成直接可用能力。

若出現這種情況：

1. 不要立刻判斷 server 壞掉
2. 直接改走第 5 節的 Python HTTP fallback

### 6.5 `resources/read` 一開始就 406 Not Acceptable

先檢查你是不是少了：

- `Accept: application/json, text/event-stream`

對這個 ASP.NET MCP server，裸 `Content-Type: application/json` 不夠。少了正確 `Accept` header，連 `initialize` 都可能直接被拒絕。

### 6.6 `resources/read` 持續回 `exists = false`

先不要立刻怪 server。先依序檢查：

1. 你是否真的帶了 `Accept: application/json, text/event-stream`
2. 你是否真的把 `initialize` 回來的 session header 正確讀到並原樣帶回後續 request
3. 你拿去讀的 `asyncId` 是否真的是 `execute_f_sharp_code_async_routed` 回來的文字內容，而不是額外包了一層 JSON 後解析錯誤

如果 `exists = true` 但長時間 `Running`，那是另一種情況：

1. 代表 server 端 async job 存在
2. 問題不是 resource surface 消失
3. 這時應增加輪詢時間，而不是改判成 `exists=false` 類型錯誤

### 6.7 Bash command parsing error

如果你看到這類錯誤：

1. `Bash command parsing error`
2. heredoc syntax error
3. 多行 F# 片段在 shell 中爆炸

那通常不是 MCP server 壞掉，而是你把：

1. shell quoting
2. JSON escaping
3. 多行 F# code

硬塞進同一條 `curl -d '...'` 指令造成的。

做法：

1. 把 code 寫到檔案
2. 用 Python 讀檔送 HTTP request
3. 不要直接 inline 多行 code

### 6.8 `get_async_status` 可用時優先使用

若你連到的部署版本已包含 tool：

- `get_async_status`

那輪詢順序應優先改成：

1. `execute_f_sharp_code_async_routed`
2. `get_async_status(asyncId)`
3. 完成後 `evaluate_f_sharp_expression_routed`

這樣可以完全避開 client 端不擅長 `resources/read` 的問題。

### 6.9 不要直接用舊的 host/session id

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

---

## 8. 單次 Prompt 範本

若要把這份文件交給另一個較弱的 agent，建議直接使用類似下面的 prompt：

```text
Read /workspace/home/mcp/FSharp.MCP.DevKit/doc/E2EScenarioTest.md first and follow it exactly.

Do not modify the original generate_real_charts.inspect_930k_vs_30k.fsx file.
Only transform the code string you send to MCP.

Use the deployed fsharp-devkit MCP server to:
1. create a fresh remote net10 host
2. create a fresh session on that host
3. execute lines 1~76 of the target fsx asynchronously
4. wait until async execution is completed
5. evaluate cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c

If direct MCP tool support is missing, use the pure HTTP JSON-RPC fallback exactly as documented.

Important:
- rewrite /workspace/home/... paths to the remote container-visible /gemini4/... paths where required
- include Accept: application/json, text/event-stream in HTTP fallback
- read mcp-session-id case-insensitively
- do not stop just because async status stays Running for under several minutes
```
