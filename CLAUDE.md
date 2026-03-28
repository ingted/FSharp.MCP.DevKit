# FSharp.MCP.DevKit — Claude 使用指南

## MCP Server 連線

- URL: `http://10.28.112.140:15000/mcp`（HTTP Streamable MCP）
- 在 `~/.claude/settings.json` 中以 `type: http` 登錄
- ProcSupervisor REST API: `http://10.28.112.140:6001`

## 建立 Out-of-Process FSI Host

### 使用 `create_fsi_host` tool

**必填參數：**

| 參數 | 值 |
|------|----|
| `agentId` | `"claude"` （或其他 agent 識別） |
| `hostKind` | `"net10"` |
| `executablePath` | `"dotnet"` |
| `arguments` | 見下方 |

**正確的 `arguments`（關鍵）：**

```
exec --runtimeconfig /app/Akka.Proc.Supervisor.runtimeconfig.json --depsfile /app/Akka.Proc.Supervisor.deps.json /app/Akka.Proc.Supervisor.dll --mode procnode --systemname fsi-proc --host 10.28.112.140 --port 0 --supervisor akka.tcp://proc-system@10.28.112.140:8110/user/proc-supervisor --procid <hostId>
```

**每個 host 的 `--procid` 必須與 `hostId` 一致。**

### 完整範例

```
create_fsi_host(
  agentId = "claude",
  hostId = "fsihost1",
  hostKind = "net10",
  executablePath = "dotnet",
  arguments = "exec --runtimeconfig /app/Akka.Proc.Supervisor.runtimeconfig.json --depsfile /app/Akka.Proc.Supervisor.deps.json /app/Akka.Proc.Supervisor.dll --mode procnode --systemname fsi-proc --host 10.28.112.140 --port 0 --supervisor akka.tcp://proc-system@10.28.112.140:8110/user/proc-supervisor --procid fsihost1"
)
```

可連續建立多個 host，彼此不互相干擾。

---

## 重要技術細節與踩坑紀錄

### 問題：第二個 host 建立失敗（DData WriteMajority timeout）

**症狀：** `create_fsi_host` 回傳 `AskTimeoutException: Timed out waiting for ProcSupervisor StartProc response`

**根本原因：**
- Procnode 以 `--seed akka.tcp://proc-system@...:8110` 加入 supervisor 的 Akka cluster
- Procnode 加入後成為 cluster member，但沒有啟動 `DistributedData.Replicator`
- Akka Cluster Sharding 的 ShardCoordinator 寫入 DData 時需要 `WriteMajority`（所有 member 都要 ACK）
- Procnode 無法 ACK → DData write 每 5000ms 失敗一次，無限 retry
- `updating-state-timeout = 5000ms`（Akka 預設值）始終無法完成

**正確做法：procnode 不加入 proc-system cluster**

關鍵：**不傳 `--seed`**，且使用不同的 `--systemname`（如 `fsi-proc`）：
- `joinCluster` 在 seeds 為空時執行 `cluster.Join(cluster.SelfAddress)` → self-join → 獨立 1-member cluster
- proc-system cluster 永遠只有 supervisor 這 1 個 member
- `WriteMajority = 1` → DData 寫入永遠立即成功

### 錯誤的做法

```
# 錯誤：加入 proc-system cluster 導致 DData 阻塞
--systemname proc-system --seed akka.tcp://proc-system@10.28.112.140:8110
```

### 注意事項

- 即使 kill 掉已加入 cluster 的 procnode，DData WriteAggregator 的 stuck state 仍可能殘留，需重啟整個 container
- DData 無 durable 設定（無 LMDB），restart container 即可清除所有 in-memory state

### Black-box 使用限制

`create_fsi_host` 目前需要 caller 知道：
- Container 內的 binary 路徑（`/app/Akka.Proc.Supervisor.dll`）
- Supervisor 的 Akka address（`akka.tcp://proc-system@10.28.112.140:8110/user/proc-supervisor`）
- 正確的 `--systemname`（不能是 `proc-system`）

這些應由 server 自行 auto-discover（參考 `/api/proc/nodes/start-default`），是目前工具設計的已知缺陷。

---

## FSI Session 工作流程

### 建立 Session

```
create_fsi_session(
  agentId = "claude",
  hostId  = "fsihost1",
  sessionId = "host1-session1"   // 自訂 ID，同一 host 內唯一
)
```

### 執行程式碼（指定路由）

```
execute_f_sharp_code_routed(
  agentId   = "claude",
  hostId    = "fsihost1",
  sessionId = "host1-session1",
  code      = "...",
  timeoutSeconds = 60
)
```

### Session 管理

- 同一 host 可建立多個 session（狀態彼此隔離）
- `list_fsi_sessions` / `list_fsi_hosts` 可查詢現有資源
- Session 狀態為 `SessionReady` 才能執行
- `reset_fsi_session_routed` 可重置 session 狀態

---

## 實測結果摘要（2026-03-27）

### 測試配置

- 2 個 out-of-process FSI host：`fsihost1`、`fsihost2`
- 每個 host 3 個 session，共 6 個 session

### 成功案例

| Session | 測試內容 | 結果 |
|---------|---------|------|
| fsihost1 / host1-session1 | Binary Search Tree（DU + 遞迴） | ✓ |
| fsihost1 / host1-session2 | `#r "nuget: Newtonsoft.Json, 13.0.3"` + JSON 序列化 | ✓ |
| fsihost1 / host1-session3 | `#if INTERACTIVE` / `#else` + Maybe monad（CE） | ✓ |
| fsihost2 / host2-session1 | `Async.Parallel` + `Async.RunSynchronously` | ✓ |
| fsihost2 / host2-session2 | Active patterns（`(|Even|Odd|)`）+ `List.map` | ✓ |
| fsihost2 / host2-session3 | `#r "nuget: Newtonsoft.Json, 13.0.3"` + Record 序列化 | ✓ |

### NuGet 套件相容性

- **可用（從 package cache 快速載入）：** `Newtonsoft.Json 13.0.3`
- **失敗（下載或相容性問題）：** `FSharp.Data 6.4.0`、`MathNet.Numerics 5.0.0`
- 若 `#r "nuget: ..."` 在某 session 失敗（`Stopped due to error`），不代表其他 session 或其他套件也會失敗
- 失敗後同一 session 可繼續用已知可用的套件重試

### `#if INTERACTIVE` 行為

FSI host 預設定義了 `INTERACTIVE` symbol，所以：
```fsharp
#if INTERACTIVE
printfn "In FSI"   // 這行會執行
#else
printfn "Compiled" // 這行不執行
#endif
```

---

## Docker 部署

```bash
# 使用 build.host.sh（--no-cache，完整 rebuild + 自動重啟服務）
cd /workspace/home/mcp/docker/FSharp.MCP.DevKit
bash build.host.sh

# 或手動建置（context root 必須是 /workspace/home）
cd /workspace/home
sudo docker build -f mcp/docker/FSharp.MCP.DevKit/Dockerfile -t fsharp-mcp-devkit:local .
```

重要環境變數（由 `entrypoint.sh` 讀取）：
- `FSI_PROC_SUPERVISOR_HOST` — supervisor 綁定的 IP
- `FSI_PROC_SUPERVISOR_PORT` — Akka port（預設 `8110`）
- `FSI_PROC_SUPERVISOR_PATH` — 完整 Akka actor path（`akka.tcp://proc-system@<HOST>:8110/user/proc-supervisor`）
