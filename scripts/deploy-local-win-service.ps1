[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $false)]
    [string]$DeployRoot = "$env:ProgramData\PulseTrade\Services",

    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [int]$ServerPort = 5000,

    [Parameter(Mandatory = $false)]
    [string]$ServiceName = "fsharp-devkit",

    [Parameter(Mandatory = $false)]
    [string]$DisplayName = "F# MCP DevKit Server",

    [Parameter(Mandatory = $false)]
    [switch]$SkipPublish,

    [Parameter(Mandatory = $false)]
    [switch]$SkipStart,

    [Parameter(Mandatory = $false)]
    [switch]$RecreateService
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[deploy-local-win-service] $Message" -ForegroundColor Cyan
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
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
$deployRootPath = [System.IO.Path]::GetFullPath($DeployRoot)
$targetServerDir = Join-Path $deployRootPath "fsharp-devkit"
$healthUrl = "http://localhost:$ServerPort/healthz"
$serverUrls = "http://0.0.0.0:$ServerPort"
$dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source

if (-not (Test-IsAdministrator)) {
    throw "This script must be run as Administrator to configure Windows services."
}

if (-not $PSCmdlet.ShouldProcess($deployRootPath, "Deploy FSharp.MCP.DevKit.Server as Windows service")) {
    return
}

New-Item -ItemType Directory -Force -Path $deployRootPath, $targetServerDir | Out-Null

Stop-ServiceIfExists -Name $ServiceName

if ($RecreateService) {
    Remove-ServiceRegistration -Name $ServiceName
}

if (-not $SkipPublish) {
    $publishArgs = @(
        "publish",
        $serverProject,
        "-c", $Configuration,
        "-f", "net10.0",
        "-p:SelfContained=false",
        "-o", $targetServerDir
    )

    Write-Step ("dotnet " + ($publishArgs -join " "))
    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed: $serverProject"
    }
}

$serverExe = Join-Path $targetServerDir "FSharp.MCP.DevKit.exe"
$serverDll = Join-Path $targetServerDir "FSharp.MCP.DevKit.dll"

if (Test-Path -LiteralPath $serverExe) {
    $serviceCommand = '"' + $serverExe + '" --service-name "' + $ServiceName + '" --urls "' + $serverUrls + '"'
}
elseif (Test-Path -LiteralPath $serverDll) {
    $serviceCommand = '"' + $dotnetExe + '" "' + $serverDll + '" --service-name "' + $ServiceName + '" --urls "' + $serverUrls + '"'
}
else {
    throw "Server executable not found: $serverExe or $serverDll"
}

Write-Step "Configuring Windows service: $ServiceName"
Ensure-ServiceConfigured `
    -Name $ServiceName `
    -DisplayName $DisplayName `
    -BinaryPath $serviceCommand `
    -Description "FSharp MCP DevKit Server"

if (-not $SkipStart) {
    Write-Step "Starting Windows service: $ServiceName"
    Start-Service -Name $ServiceName

    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(60))

    Write-Step "Waiting for health endpoint: $healthUrl"
    $health = Wait-Health -Url $healthUrl -TimeoutSeconds 30
    Write-Host ("Health: " + ($health | ConvertTo-Json -Compress)) -ForegroundColor Green
}

Write-Step "Done."
Write-Host "Service name : $ServiceName" -ForegroundColor Green
Write-Host "Deploy dir   : $targetServerDir" -ForegroundColor Green
Write-Host "Health URL   : $healthUrl" -ForegroundColor Green
