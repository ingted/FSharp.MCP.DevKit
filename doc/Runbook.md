# Runbook

## 啟動順序

1. `fsihost`
2. `fsharp-devkit`

`fsharp-devkit` 會相依 `fsihost`，部署腳本也會先啟動 `fsihost` 再啟動 server。

## 健康檢查

- HTTP:
  - `http://localhost:5000/healthz`
- 預期回應欄位：
  - `status`
  - `transport`
  - `isWindowsService`
  - `serviceName`

## 常用維運指令

```powershell
Get-Service fsihost, fsharp-devkit
```

```powershell
Restart-Service fsihost
Restart-Service fsharp-devkit
```

```powershell
Invoke-RestMethod http://localhost:5000/healthz
```

## 異常處理

### `fsihost` 無法啟動

- 檢查遠端主機是否安裝 .NET Framework 4.7.2 以上
- 檢查 `<RemoteRoot>\fsihost\FSharp.MCP.DevKit.FsiHost.exe` 是否存在
- 檢查 `akka.conf` 是否隨 artifact 一起複製

### `fsharp-devkit` 無法啟動

- 先確認 `fsihost` 已進入 `Running`
- 檢查 `<RemoteRoot>\fsharp-devkit\FSharp.MCP.DevKit.exe` 是否存在
- 檢查 `http://localhost:<port>/healthz` 是否被其他程序占用或被防火牆阻擋

### 部署腳本失敗

- 先用 `-WhatIf` 確認參數組合
- 若已手動 publish，可改用 `-SkipPublish`
- 若遠端服務卡住，先停服務再重跑：

```powershell
Invoke-Command -ComputerName 10.36.205.160 {
    Stop-Service fsharp-devkit -Force -ErrorAction SilentlyContinue
    Stop-Service fsihost -Force -ErrorAction SilentlyContinue
}
```

## 回滾

- 本輪腳本採固定目錄覆寫部署：
  - `<RemoteRoot>\fsihost`
  - `<RemoteRoot>\fsharp-devkit`
- 回滾方式：
  1. 重新準備上一版 artifact
  2. 以 `-SkipPublish` 指向上一版 artifact 重新執行部署腳本

## Scripts 盤點

- `scripts/deploy-remote-services.ps1`：正式部署腳本
- `scripts/fsi-*.ps1`、`scripts/fsi-exec.cmd`、`scripts/build-packages.sh`：目前僅為 placeholder / stub，不可當成正式 MCP client 工具使用
