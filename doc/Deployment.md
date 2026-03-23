# Deployment

## 目標拓樸

- `fsharp-devkit`
  - runtime: `.NET 10`
  - role: 提供 MCP server、async queue、HTTP `/mcp`、`/healthz`
- legacy `FsiHost`
  - runtime: `net472`
  - role: 只作為 artifact staged 到部署目錄，供後續 `create_fsi_host` 指向該 exe 啟動
  - 不會在部署時預先註冊成 Windows service

## 正式部署腳本

- 位置：`scripts/deploy-remote-services.ps1`
- 前提：
  - 本機可 `Invoke-Command -ComputerName <target> { hostname }`
  - 遠端主機為 64-bit Windows
  - 遠端已安裝 .NET Framework 4.7.2 以上
  - 執行帳號具備遠端檔案複製與 Windows service 註冊權限

## 預設安裝路徑

給定 `-RemoteRoot <path>` 後，腳本會部署到：

- `<RemoteRoot>\hosts\netfx`
- `<RemoteRoot>\fsharp-devkit`
- `RemoteRoot` 必須是遠端主機上的本機固定磁碟路徑，例如 `C:\services\FSharp.MCP.DevKit.Async`
- `ServerPort` 必須是遠端主機上尚未被占用的 TCP port

## 預設服務名稱

- `fsharp-devkit`
- 若指定 `-RecreateServices`，腳本會在重部署時先刪除既有 service registration，再重新建立
- 若遠端曾有舊版 `fsihost` service，腳本也會先 stop；若指定 `-RecreateServices`，也會一併刪除舊 registration

## 常用指令

### 1. 由原始碼直接 publish 並部署

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-remote-services.ps1 `
  -ComputerName 10.36.205.160 `
  -RemoteRoot C:\services\FSharp.MCP.DevKit.Async `
  -Configuration Release `
  -ServerPort 5000
```

### 2. 重用既有 artifact 目錄部署

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-remote-services.ps1 `
  -ComputerName 10.36.205.160 `
  -RemoteRoot C:\services\FSharp.MCP.DevKit.Async `
  -SkipPublish `
  -FsiHostArtifactPath .\artifacts\deploy-check\fsihost `
  -ServerArtifactPath .\artifacts\deploy-check\fsharp-devkit
```

### 3. 先做 dry-run

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-remote-services.ps1 `
  -ComputerName 10.36.205.160 `
  -RemoteRoot C:\services\FSharp.MCP.DevKit.Async `
  -SkipPublish `
  -FsiHostArtifactPath .\artifacts\deploy-check\fsihost `
  -ServerArtifactPath .\artifacts\deploy-check\fsharp-devkit `
  -WhatIf
```

### 4. 強制刪除並重建 service

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-remote-services.ps1 `
  -ComputerName 10.36.205.160 `
  -RemoteRoot C:\services\FSharp.MCP.DevKit.Async `
  -Configuration Release `
  -ServerPort 5010 `
  -RecreateServices
```

## 驗證

- 服務啟動後，腳本會驗證：
  - `fsharp-devkit` service 狀態為 `Running`
  - `http://localhost:<ServerPort>/healthz` 可回應 JSON
  - `<RemoteRoot>\hosts\netfx\FSharp.MCP.DevKit.FsiHost.exe` 存在，可供之後 `create_fsi_host` 使用

## 本輪驗證證據

- `log/20260319140523.fsihost-publish.op_log`
- `log/20260319140523.server-publish.op_log`
- `log/20260319140523.deploy-script-syntax.op_log`
- `log/20260319140523.deploy-script-whatif.op_log`
