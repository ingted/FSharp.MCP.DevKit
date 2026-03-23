# Runbook

## 啟動順序

1. `fsharp-devkit`

目前部署腳本只會安裝與啟動 `fsharp-devkit`。  
legacy `FsiHost` 只會被 staged 到部署目錄，之後若要建立 out-of-proc `netfx` host，再由 `create_fsi_host` 指向該 exe 啟動。

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
Get-Service fsharp-devkit
```

```powershell
Restart-Service fsharp-devkit
```

```powershell
Invoke-RestMethod http://localhost:5000/healthz
```

## 異常處理

### `fsharp-devkit` 無法啟動

- 檢查 `<RemoteRoot>\fsharp-devkit\FSharp.MCP.DevKit.exe` 是否存在
- 檢查 `http://localhost:<port>/healthz` 是否被其他程序占用或被防火牆阻擋
- 檢查是否有舊版 `fsihost` service 或其他程序占用資源

### legacy `FsiHost` artifact 不存在

- 檢查遠端主機是否安裝 .NET Framework 4.7.2 以上
- 檢查 `<RemoteRoot>\hosts\netfx\FSharp.MCP.DevKit.FsiHost.exe` 是否存在
- 檢查相關 `akka*.conf` 是否隨 artifact 一起複製

### 部署腳本失敗

- `RemoteRoot` 必須放在遠端主機可寫入的本機固定磁碟，例如 `C:\services\FSharp.MCP.DevKit.Async`
- 若目標磁碟是 `Removable`、`IsReady = False` 或容量為 `0`，部署腳本會在複製前直接失敗
- `ServerPort` 若已被其他程序占用，部署腳本會在清空目錄前直接失敗，需改用其他 port
- 若懷疑 service registration 本身已壞掉，可加 `-RecreateServices` 先刪除再重建服務
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
  - `<RemoteRoot>\hosts\netfx`
  - `<RemoteRoot>\fsharp-devkit`
- 回滾方式：
  1. 重新準備上一版 artifact
  2. 以 `-SkipPublish` 指向上一版 artifact 重新執行部署腳本

## Scripts 盤點

- `scripts/deploy-remote-services.ps1`：正式部署腳本
- `scripts/fsi-*.ps1`、`scripts/fsi-exec.cmd`、`scripts/build-packages.sh`：目前僅為 placeholder / stub，不可當成正式 MCP client 工具使用
