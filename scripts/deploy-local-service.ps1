[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$DeployRoot,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string]$RuntimeIdentifier = "win-x64",

    [Parameter(Mandatory = $false)]
    [int]$ServerPort = 5000,

    [Parameter(Mandatory = $false)]
    [string]$ServerServiceName = "fsharp-devkit",

    [Parameter(Mandatory = $false)]
    [string]$LegacyFsiHostServiceName = "fsihost",

    [Parameter(Mandatory = $false)]
    [string]$ServerDisplayName = "F# MCP DevKit Server",

    [Parameter(Mandatory = $false)]
    [string]$LegacyNetFxHostDirName = "hosts\netfx",

    [Parameter(Mandatory = $false)]
    [switch]$SkipPublish,

    [Parameter(Mandatory = $false)]
    [switch]$SkipStart,

    [Parameter(Mandatory = $false)]
    [switch]$RecreateServices,

    [Parameter(Mandatory = $false)]
    [switch]$SkipLegacyNetFxHostPublish
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[deploy-local] $Message" -ForegroundColor Cyan
}

function Invoke-DotnetPublish {
    param(
        [string]$ProjectPath,
        [string[]]$Arguments
    )

    $command = @("publish", $ProjectPath) + $Arguments
    Write-Step ("dotnet " + ($command -join " "))
    & dotnet @command

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed: $ProjectPath"
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-ServicePortAvailable {
    param([int]$Port)

    $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1

    if (-not $listener) {
        return
    }

    $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    $processName = if ($process) { $process.ProcessName } else { "unknown" }
    throw "Port $Port is already used by PID $($listener.OwningProcess) ($processName)."
}

function Stop-ServiceIfExists {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue

    if ($service -and $service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Step "Stopping service: $Name"
        Stop-Service -Name $Name -Force -ErrorAction Stop
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(60))
    }
}

function Remove-ServiceRegistration {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue

    if (-not $service) {
        return
    }

    Write-Step "Deleting service registration: $Name"
    & sc.exe delete $Name | Out-Null

    $deadline = [DateTime]::UtcNow.AddSeconds(30)

    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 1

        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
            return
        }
    }

    throw "Timed out waiting for service registration to be removed: $Name"
}

function Ensure-ServiceConfigured {
    param(
        [string]$Name,
        [string]$DisplayName,
        [string]$BinaryPath,
        [string]$Description
    )

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue

    $args =
        if ($service) {
            @("config", $Name, "binPath=", $BinaryPath, "start=", "auto", "DisplayName=", $DisplayName)
        }
        else {
            @("create", $Name, "binPath=", $BinaryPath, "start=", "auto", "DisplayName=", $DisplayName)
        }

    & sc.exe @args | Out-Null
    & sc.exe description $Name $Description | Out-Null
    & sc.exe failure $Name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
}

function Wait-Health {
    param(
        [string]$Url,
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            return Invoke-RestMethod -Uri $Url -TimeoutSec 5
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }

    throw "Timed out waiting for health endpoint: $Url"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$serverProject = Join-Path $repoRoot "src\FSharp.MCP.DevKit.Server\FSharp.MCP.DevKit.Server.fsproj"
$legacyNetFxHostProject = Join-Path $repoRoot "src\FSharp.MCP.DevKit.FsiHost\FSharp.MCP.DevKit.FsiHost.fsproj"

$resolvedDeployRoot = (Resolve-Path -LiteralPath $DeployRoot -ErrorAction SilentlyContinue)

if ($resolvedDeployRoot) {
    $deployRootPath = $resolvedDeployRoot.Path
}
else {
    $deployRootPath = [System.IO.Path]::GetFullPath($DeployRoot)
}

$targetServerDir = Join-Path $deployRootPath "fsharp-devkit"
$targetLegacyNetFxHostDir = Join-Path $deployRootPath $LegacyNetFxHostDirName
$serverUrls = "http://0.0.0.0:$ServerPort"
$healthUrl = "http://localhost:$ServerPort/healthz"

if (-not $PSCmdlet.ShouldProcess($deployRootPath, "Deploy local FSharp.MCP.DevKit Windows service")) {
    return
}

if (-not (Test-IsAdministrator)) {
    throw "This script must be run as Administrator to configure Windows services."
}

New-Item -ItemType Directory -Force -Path $deployRootPath, $targetServerDir | Out-Null

if (-not $SkipLegacyNetFxHostPublish) {
    New-Item -ItemType Directory -Force -Path $targetLegacyNetFxHostDir | Out-Null
}

Stop-ServiceIfExists -Name $ServerServiceName
Stop-ServiceIfExists -Name $LegacyFsiHostServiceName

if ($RecreateServices) {
    Remove-ServiceRegistration -Name $ServerServiceName
    Remove-ServiceRegistration -Name $LegacyFsiHostServiceName
}

if (-not $SkipPublish) {
    Write-Step "Publishing server artifacts"
    Invoke-DotnetPublish -ProjectPath $serverProject -Arguments @(
        "-c", $Configuration,
        "-f", "net10.0",
        "-r", $RuntimeIdentifier,
        "--self-contained", "true",
        "-o", $targetServerDir
    )

    if (-not $SkipLegacyNetFxHostPublish) {
        Write-Step "Publishing legacy netfx FsiHost artifacts"
        Invoke-DotnetPublish -ProjectPath $legacyNetFxHostProject -Arguments @(
            "-c", $Configuration,
            "-f", "net472",
            "-o", $targetLegacyNetFxHostDir
        )
    }
}

Assert-ServicePortAvailable -Port $ServerPort

$serverExe = Join-Path $targetServerDir "FSharp.MCP.DevKit.exe"

if (-not (Test-Path -LiteralPath $serverExe)) {
    throw "Server executable not found: $serverExe"
}

$serverBinPath = '"' + $serverExe + '" --service-name "' + $ServerServiceName + '" --urls "' + $serverUrls + '"'

Write-Step "Configuring Windows service: $ServerServiceName"
Ensure-ServiceConfigured `
    -Name $ServerServiceName `
    -DisplayName $ServerDisplayName `
    -BinaryPath $serverBinPath `
    -Description "FSharp MCP DevKit Server"

if (-not $SkipStart) {
    Write-Step "Starting Windows service: $ServerServiceName"
    Start-Service -Name $ServerServiceName

    $service = Get-Service -Name $ServerServiceName -ErrorAction Stop
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(60))

    Write-Step "Waiting for health endpoint: $healthUrl"
    $health = Wait-Health -Url $healthUrl -TimeoutSeconds 30
    Write-Host ("Health: " + ($health | ConvertTo-Json -Compress)) -ForegroundColor Green
}

Write-Step "Local deployment completed."
Write-Host "Server service name : $ServerServiceName" -ForegroundColor Green
Write-Host "Server directory    : $targetServerDir" -ForegroundColor Green
Write-Host "Server health URL   : $healthUrl" -ForegroundColor Green

if (-not $SkipLegacyNetFxHostPublish) {
    $legacyNetFxHostExe = Join-Path $targetLegacyNetFxHostDir "FSharp.MCP.DevKit.FsiHost.exe"
    Write-Host "Legacy netfx host   : $legacyNetFxHostExe" -ForegroundColor Green
    Write-Host "Note                : legacy netfx host is staged as an artifact only; it is not registered as a Windows service by this script." -ForegroundColor Yellow
}

