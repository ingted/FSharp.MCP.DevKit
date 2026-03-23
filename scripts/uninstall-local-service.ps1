[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $false)]
    [string]$DeployRoot,

    [Parameter(Mandatory = $false)]
    [string]$ServerServiceName = "fsharp-devkit",

    [Parameter(Mandatory = $false)]
    [string]$LegacyFsiHostServiceName = "fsihost",

    [Parameter(Mandatory = $false)]
    [switch]$RemoveDeployRoot
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[uninstall-local] $Message" -ForegroundColor Cyan
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

if (-not $PSCmdlet.ShouldProcess("localhost", "Uninstall local FSharp.MCP.DevKit Windows services")) {
    return
}

if (-not (Test-IsAdministrator)) {
    throw "This script must be run as Administrator to remove Windows services."
}

Stop-ServiceIfExists -Name $ServerServiceName
Stop-ServiceIfExists -Name $LegacyFsiHostServiceName

Remove-ServiceRegistration -Name $ServerServiceName
Remove-ServiceRegistration -Name $LegacyFsiHostServiceName

if ($RemoveDeployRoot) {
    if ([string]::IsNullOrWhiteSpace($DeployRoot)) {
        throw "-DeployRoot is required when -RemoveDeployRoot is specified."
    }

    $deployRootPath = [System.IO.Path]::GetFullPath($DeployRoot)

    if (Test-Path -LiteralPath $deployRootPath) {
        Write-Step "Removing deploy root: $deployRootPath"
        Remove-Item -LiteralPath $deployRootPath -Recurse -Force
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($DeployRoot)) {
    $deployRootPath = [System.IO.Path]::GetFullPath($DeployRoot)
    Write-Host "Deploy root kept: $deployRootPath" -ForegroundColor Yellow
}

Write-Step "Local uninstall completed."
