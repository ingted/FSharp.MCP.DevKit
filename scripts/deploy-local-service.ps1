#!/usr/bin/env pwsh
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$DeployRoot, # 本地部署的目標根目錄

    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string]$RuntimeIdentifier = "win-x64",

    [Parameter(Mandatory = $false)]
    [int]$ServerPort = 5000,

    [Parameter(Mandatory = $false)]
    [string]$FsiHostServiceName = "fsihost",

    [Parameter(Mandatory = $false)]
    [string]$ServerServiceName = "fsharp-devkit",

    [Parameter(Mandatory = $false)]
    [string]$FsiHostDisplayName = "F# MCP DevKit FsiHost",

    [Parameter(Mandatory = $false)]
    [string]$ServerDisplayName = "F# MCP DevKit Server",

    [Parameter(Mandatory = $false)]
    [switch]$SkipPublish,

    [Parameter(Mandatory = $false)]
    [switch]$SkipStart,

    [Parameter(Mandatory = $false)]
    [switch]$RecreateServices
)

$ErrorActionPreference = "Stop"

# --- 輔助函式 ---

function Write-Step {
    param([string]$Message)
    Write-Host "[deploy-local] $Message" -ForegroundColor Cyan
}

function Invoke-DotnetPublish {
    param([string]$ProjectPath, [string[]]$Arguments)
    $command = @("publish", $ProjectPath) + $Arguments
    Write-Step ("dotnet " + ($command -join " "))
    & dotnet @command
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $ProjectPath" }
}

function Assert-ServicePortAvailable {
    param([int]$Port)
    $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
        throw "Port $Port is used by PID $($listener.OwningProcess) ($($process.ProcessName))."
    }
}

function Ensure-ServiceConfigured {
    param([string]$Name, [string]$DisplayName, [string]$BinaryPath, [string[]]$DependsOn, [string]$Description)
    
    $dep = if ($DependsOn) { $DependsOn -join "/" } else { $null }
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    
    $args = if ($service) {
        @("config", $Name, "binPath=", $BinaryPath, "start=", "auto", "DisplayName=", $DisplayName)
    } else {
        @("create", $Name, "binPath=", $BinaryPath, "start=", "auto", "DisplayName=", $DisplayName)
    }
    if ($dep) { $args += @("depend=", $dep) }

    & sc.exe @args
    & sc.exe description $Name $Description
    & sc.exe failure $Name reset= 86400 actions= restart/60000/restart/60000/restart/60000
}

# --- 主程序開始 ---

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$fsiHostProject = Join-Path $repoRoot "src\FSharp.MCP.DevKit.FsiHost\FSharp.MCP.DevKit.FsiHost.fsproj"
$serverProject = Join-Path $repoRoot "src\FSharp.MCP.DevKit.Server\FSharp.MCP.DevKit.Server.fsproj"

$targetFsiHostDir = Join-Path $DeployRoot "fsihost"
$targetServerDir = Join-Path $DeployRoot "fsharp-devkit"
$serverUrls = "http://0.0.0.0:$ServerPort"
$healthUrl = "http://localhost:$ServerPort/healthz"

if (-not $PSCmdlet.ShouldProcess($DeployRoot, "在本地部署 MCP 服務")) { return }

# 1. 檢查權限 (建立服務需要管理員權限)
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "此腳本需要以管理員權限執行才能配置 Windows 服務。"
}

# 2. 停止現有服務
foreach ($svc in @($ServerServiceName, $FsiHostServiceName)) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s -and $s.Status -ne "Stopped") {
        Write-Step "正在停止服務: $svc"
        Stop-Service -Name $svc -Force
        $s.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
}

if ($RecreateServices) {
    foreach ($svc in @($ServerServiceName, $FsiHostServiceName)) {
        if (Get-Service -Name $svc -ErrorAction SilentlyContinue) {
            Write-Step "正在刪除服務註冊: $svc"
            & sc.exe delete $svc
            Start-Sleep -Seconds 2
        }
    }
}

# 3. 編譯項目 (Publish)
if (-not $SkipPublish) {
    Write-Step "正在編譯並發佈項目..."
    Invoke-DotnetPublish -ProjectPath $fsiHostProject -Arguments @("-c", $Configuration, "-f", "net472", "-o", $targetFsiHostDir)
    Invoke-DotnetPublish -ProjectPath $serverProject -Arguments @("-c", $Configuration, "-f", "net10.0", "-r", $RuntimeIdentifier, "--self-contained", "true", "-o", $targetServerDir)
}

# 4. 配置 Windows 服務
Assert-ServicePortAvailable -Port $ServerPort

$fsiHostExe = Join-Path $targetFsiHostDir "FSharp.MCP.DevKit.FsiHost.exe"
$serverExe = Join-Path $targetServerDir "FSharp.MCP.DevKit.exe"

$fsiBinPath = "`"$fsiHostExe`" --service --service-name `"$FsiHostServiceName`""
$serverBinPath = "`"$serverExe`" --service-name `"$ServerServiceName`" --urls `"$serverUrls`""

Write-Step "正在配置 Windows 服務..."
Ensure-ServiceConfigured -Name $FsiHostServiceName -DisplayName $FsiHostDisplayName -BinaryPath $fsiBinPath -Description "FSI Host Service"
Ensure-ServiceConfigured -Name $ServerServiceName -DisplayName $ServerDisplayName -BinaryPath $serverBinPath -DependsOn @($FsiHostServiceName) -Description "MCP Server"

# 5. 啟動服務
if (-not $SkipStart) {
    Write-Step "正在啟動服務..."
    Start-Service -Name $FsiHostServiceName
    Start-Service -Name $ServerServiceName
    
    Write-Step "等待健康檢查: $healthUrl"
    $timeout = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $timeout) {
        try {
            $resp = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5
            Write-Host "服務已就緒: $($resp | ConvertTo-Json -Compress)" -ForegroundColor Green
            break
        } catch { Start-Sleep -Seconds 2 }
    }
}

Write-Step "本地部署完成。"