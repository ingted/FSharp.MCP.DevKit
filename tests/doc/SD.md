# Test Project SD

## 測試專案結構

本輪先把 tests 專案整理成下列結構：

```text
tests/
  FSharp.MCP.DevKit.Tests.fsproj
  SmartSymbolDetectionServiceTests.fs
  BackendSelectorTests.fs
  BackendAdaptersTests.fs
  RemoteMessageContractTests.fs
  doc/
    SA.md
    SD.md
    WBS.md
```

## 測試模組設計

### 1. `BackendSelectorTests.fs`

目的：

- 驗證 `HostKind -> BackendKind -> IFsiExecutionBackend` 的 resolve 規則。
- 驗證未註冊 backend 時會 fail fast。

需要的 test double：

```fsharp
type FakeBackend(kind: BackendKind) =
    interface IFsiExecutionBackend with
        member _.BackendKind = kind
        member _.Execute _ = Task.FromException<FsiExecutionRecord>(NotImplementedException())
        member _.GetSessionState _ = Task.FromException<SessionRecord>(NotImplementedException())
        member _.ResetSession _ = Task.FromException<FsiExecutionRecord>(NotImplementedException())
        member _.RestartHost _ = Task.FromException<unit>(NotImplementedException())
        member _.HealthCheck _ = Task.FromException<BackendHealth>(NotImplementedException())
```

案例：

- `Resolve(InProcHost)` 取得 `InProc`
- `Resolve(NetFxHost)` 取得 `NetFxRemote`
- `Resolve(Net10Host)` 取得 `Net10Remote`
- 缺少對應 backend 時丟出例外

### 2. `BackendAdaptersTests.fs`

目的：

- 驗證 `PipeResponse -> FsiResult`
- 驗證 `inferRawErrorType`
- 驗證 `toExecutionRecord`

案例：

- 含 diagnostics/value 的 `PipeResponse` 可正確轉為 `FsiResult`
- 成功 response 的 `inferRawErrorType = None`
- 有 error 但字串為空時回 `UnknownRemoteError`
- 有 error 文字時回 `RemoteExecutionError`
- `toExecutionRecord` 會保留 `RequestId/AgentId/HostId/SessionId/BackendKind`

### 3. `RemoteMessageContractTests.fs`

目的：

- 驗證 route-aware DTO 的欄位可正常建立與讀取。
- 確認 optional 欄位的預期使用方式。

案例：

- `FsiRemoteCommandRequest` 可帶 `Route` 與 `TimeoutMs`
- `FsiRemoteResult` 可帶 `Value` 與 `RawErrorType`
- `FsiRemoteCommandResponse` 可帶 `HostId` 與 `SessionId`

## 測試層級

本輪只做單元測試與契約測試，不做整合測試。

原因：

- 目前 registry / router 尚未正式落地。
- host 啟動與 ProcSupervisor 還在設計中的後續工作包。
- 本輪最適合先固定資料模型與 mapping 行為。

## 專案變更

### `FSharp.MCP.DevKit.Tests.fsproj`

需要補：

- `ProjectReference` 到 `..\src\FSharp.MCP.DevKit.Server\FSharp.MCP.DevKit.Server.fsproj`
- 新增三個 `Compile Include`

## 執行方式

開發過程先跑：

```bash
dotnet test /workspace/home/mcp/FSharp.MCP.DevKit/tests/FSharp.MCP.DevKit.Tests.fsproj -f net10.0
```

## 非目標

- 不在本輪補測 `McpFsiTools` 全部工具
- 不在本輪補 Akka actor integration tests
- 不在本輪補 HTTP / MCP end-to-end tests
