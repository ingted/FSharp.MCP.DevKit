[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $false)]
    [string]$DeployRoot = "$env:ProgramData\PulseTrade\Services",

    [Parameter(Mandatory = $false)]
    [string]$ServiceName = "fsharp-devkit",

    [Parameter(Mandatory = $false)]
    [switch]$RemoveServiceDirectory,

    [Parameter(Mandatory = $false)]
    [switch]$RemoveDeployRoot
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[uninstall-local-win-service] $Message" -ForegroundColor Cyan
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

if (-not $PSCmdlet.ShouldProcess("localhost", "Uninstall local FSharp.MCP.DevKit Windows service")) {
    return
}

if (-not (Test-IsAdministrator)) {
    throw "This script must be run as Administrator to remove Windows services."
}

$deployRootPath = [System.IO.Path]::GetFullPath($DeployRoot)
$targetServerDir = Join-Path $deployRootPath "fsharp-devkit"

if ($RemoveDeployRoot -and -not $RemoveServiceDirectory) {
    $RemoveServiceDirectory = $true
}

Stop-ServiceIfExists -Name $ServiceName
Remove-ServiceRegistration -Name $ServiceName

if ($RemoveServiceDirectory -and (Test-Path -LiteralPath $targetServerDir)) {
    Write-Step "Removing service directory: $targetServerDir"
    Remove-Item -LiteralPath $targetServerDir -Recurse -Force
}
elseif (Test-Path -LiteralPath $targetServerDir) {
    Write-Host "Service directory kept: $targetServerDir" -ForegroundColor Yellow
}

if ($RemoveDeployRoot) {
    $remainingEntries =
        if (Test-Path -LiteralPath $deployRootPath) {
            Get-ChildItem -LiteralPath $deployRootPath -Force -ErrorAction SilentlyContinue
        }
        else {
            @()
        }

    if ($remainingEntries.Count -eq 0 -and (Test-Path -LiteralPath $deployRootPath)) {
        Write-Step "Removing deploy root: $deployRootPath"
        Remove-Item -LiteralPath $deployRootPath -Force
    }
    elseif (Test-Path -LiteralPath $deployRootPath) {
        Write-Host "Deploy root not removed because other entries still exist: $deployRootPath" -ForegroundColor Yellow
    }
}

Write-Step "Done."
Write-Host "Service name : $ServiceName" -ForegroundColor Green
Write-Host "Deploy dir   : $targetServerDir" -ForegroundColor Green
Write-Host "Deploy root  : $deployRootPath" -ForegroundColor Green
