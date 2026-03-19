#!/usr/bin/env pwsh
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$ComputerName,

    [Parameter(Mandatory = $true)]
    [string]$RemoteRoot,

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
    [string]$FsiHostArtifactPath,

    [Parameter(Mandatory = $false)]
    [string]$ServerArtifactPath,

    [Parameter(Mandatory = $false)]
    [switch]$SkipPublish,

    [Parameter(Mandatory = $false)]
    [switch]$SkipStart,

    [Parameter(Mandatory = $false)]
    [switch]$KeepLocalArtifacts
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[deploy] $Message" -ForegroundColor Cyan
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

function Resolve-ArtifactDirectory {
    param(
        [string]$Path,
        [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label artifact path is required."
    }

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    $item = Get-Item -LiteralPath $resolved.Path

    if (-not $item.PSIsContainer) {
        throw "$Label artifact path must be a directory: $Path"
    }

    return $item.FullName
}

function Get-DirectorySizeBytes {
    param([string]$Path)

    $files = Get-ChildItem -LiteralPath $Path -Recurse -Force -File

    if (-not $files) {
        return [int64]0
    }

    return [int64](($files | Measure-Object -Property Length -Sum).Sum)
}

function Copy-DirectoryContentsToSession {
    param(
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [string]$LocalPath,
        [string]$RemotePath
    )

    $items = Get-ChildItem -Force -LiteralPath $LocalPath

    foreach ($item in $items) {
        Copy-Item -LiteralPath $item.FullName -Destination $RemotePath -ToSession $Session -Recurse -Force
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$fsiHostProject = Join-Path $repoRoot "src\FSharp.MCP.DevKit.FsiHost\FSharp.MCP.DevKit.FsiHost.fsproj"
$serverProject = Join-Path $repoRoot "src\FSharp.MCP.DevKit.Server\FSharp.MCP.DevKit.Server.fsproj"

$timestamp = Get-Date -Format "yyyyMMddHHmmss"
$localArtifactRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("fsharp-mcp-devkit-deploy-" + $timestamp)
$generatedLocalArtifacts = -not $SkipPublish
$localFsiHostDir = $null
$localServerDir = $null
$remoteFsiHostDir = Join-Path $RemoteRoot "fsihost"
$remoteServerDir = Join-Path $RemoteRoot "fsharp-devkit"
$serverUrls = "http://0.0.0.0:$ServerPort"
$remoteHealthUrl = "http://localhost:$ServerPort/healthz"
$requiredRemoteBytes = [int64]0
$skipStartFlag = if ($SkipStart.IsPresent) { "true" } else { "false" }

if (-not $PSCmdlet.ShouldProcess("${ComputerName}:$RemoteRoot", "Deploy fsihost and fsharp-devkit services")) {
    return
}

if ($SkipPublish) {
    $localFsiHostDir = Resolve-ArtifactDirectory -Path $FsiHostArtifactPath -Label "FsiHost"
    $localServerDir = Resolve-ArtifactDirectory -Path $ServerArtifactPath -Label "Server"
}
else {
    $localFsiHostDir = Join-Path $localArtifactRoot "fsihost"
    $localServerDir = Join-Path $localArtifactRoot "fsharp-devkit"
    New-Item -ItemType Directory -Force -Path $localFsiHostDir, $localServerDir | Out-Null

    Invoke-DotnetPublish -ProjectPath $fsiHostProject -Arguments @(
        "-c", $Configuration,
        "-f", "net472",
        "-o", $localFsiHostDir
    )

    Invoke-DotnetPublish -ProjectPath $serverProject -Arguments @(
        "-c", $Configuration,
        "-f", "net10.0",
        "-r", $RuntimeIdentifier,
        "--self-contained", "true",
        "-o", $localServerDir
    )
}

$requiredRemoteBytes =
    (Get-DirectorySizeBytes -Path $localFsiHostDir) +
    (Get-DirectorySizeBytes -Path $localServerDir)

$session = $null

try {
    Write-Step "Opening PowerShell remoting session to $ComputerName"
    $session = New-PSSession -ComputerName $ComputerName

    Invoke-Command -Session $session -ScriptBlock {
        param(
            $RemoteRoot,
            $RemoteFsiHostDir,
            $RemoteServerDir,
            $FsiHostServiceName,
            $ServerServiceName,
            $RequiredRemoteBytes,
            $ServerPort
        )

        function Clear-DirectoryContents {
            param([string]$Path)

            if (-not (Test-Path -LiteralPath $Path)) {
                New-Item -ItemType Directory -Force -Path $Path | Out-Null
                return
            }

            Get-ChildItem -Force -LiteralPath $Path | ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
            }
        }

        function Assert-DeployRootReady {
            param(
                [string]$Path,
                [int64]$RequiredBytes
            )

            if ([string]::IsNullOrWhiteSpace($Path)) {
                throw "RemoteRoot cannot be empty."
            }

            if (-not [System.IO.Path]::IsPathRooted($Path)) {
                throw "RemoteRoot must be an absolute local path: $Path"
            }

            $root = [System.IO.Path]::GetPathRoot($Path)

            if ([string]::IsNullOrWhiteSpace($root)) {
                throw "Unable to determine the drive root for RemoteRoot: $Path"
            }

            if ($root.StartsWith("\\")) {
                throw "RemoteRoot must target a local drive, not a UNC path: $Path"
            }

            $driveInfo = [System.IO.DriveInfo]::new($root)

            if (-not $driveInfo.IsReady) {
                throw "RemoteRoot drive is not ready: $root"
            }

            if ($driveInfo.DriveType -ne [System.IO.DriveType]::Fixed) {
                throw "RemoteRoot drive must be a fixed local disk. Actual type for $root is $($driveInfo.DriveType)."
            }

            if ($driveInfo.TotalSize -le 0) {
                throw "RemoteRoot drive reports an invalid size for $root."
            }

            if ($RequiredBytes -gt 0 -and $driveInfo.AvailableFreeSpace -lt $RequiredBytes) {
                throw "RemoteRoot drive does not have enough free space. Required=$RequiredBytes bytes, Available=$($driveInfo.AvailableFreeSpace) bytes, Drive=$root"
            }
        }

        function Assert-ServerPortAvailable {
            param([int]$Port)

            $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1

            if (-not $listener) {
                return
            }

            $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
            $processName = if ($process) { $process.ProcessName } else { "unknown" }

            throw "ServerPort $Port is already in use by PID $($listener.OwningProcess) ($processName). Choose another -ServerPort."
        }

        $netRelease = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" -Name Release).Release
        if ($netRelease -lt 461808) {
            throw ".NET Framework 4.7.2 or later is required on the remote machine."
        }

        if (-not [Environment]::Is64BitOperatingSystem) {
            throw "The remote machine must be 64-bit because the server publish is win-x64."
        }

        Assert-DeployRootReady -Path $RemoteRoot -RequiredBytes $RequiredRemoteBytes

        New-Item -ItemType Directory -Force -Path $RemoteRoot | Out-Null

        foreach ($serviceName in @($ServerServiceName, $FsiHostServiceName)) {
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

            if ($service -and $service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                Stop-Service -Name $serviceName -Force -ErrorAction Stop
                $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(60))
            }
        }

        Assert-ServerPortAvailable -Port $ServerPort

        Clear-DirectoryContents -Path $RemoteFsiHostDir
        Clear-DirectoryContents -Path $RemoteServerDir
    } -ArgumentList $RemoteRoot, $remoteFsiHostDir, $remoteServerDir, $FsiHostServiceName, $ServerServiceName, $requiredRemoteBytes, $ServerPort

    Write-Step "Copying fsihost artifacts to $remoteFsiHostDir"
    Copy-DirectoryContentsToSession -Session $session -LocalPath $localFsiHostDir -RemotePath $remoteFsiHostDir

    Write-Step "Copying server artifacts to $remoteServerDir"
    Copy-DirectoryContentsToSession -Session $session -LocalPath $localServerDir -RemotePath $remoteServerDir

    $remoteSummary = Invoke-Command -Session $session -ScriptBlock {
        param(
            $RemoteFsiHostDir,
            $RemoteServerDir,
            $FsiHostServiceName,
            $ServerServiceName,
            $FsiHostDisplayName,
            $ServerDisplayName,
            $ServerUrls,
            $RemoteHealthUrl,
            $SkipStartFlag
        )

        function Invoke-ServiceCommand {
            param([string[]]$Arguments)

            $output = & sc.exe @Arguments 2>&1 | Out-String

            if ($LASTEXITCODE -ne 0) {
                throw "sc.exe failed: $($Arguments -join ' ')`n$output"
            }
        }

        function Ensure-ServiceConfigured {
            param(
                [string]$Name,
                [string]$DisplayName,
                [string]$BinaryPath,
                [string[]]$DependsOn,
                [string]$Description
            )

            $dependencySpec = $null
            if ($DependsOn -and $DependsOn.Count -gt 0) {
                $dependencySpec = ($DependsOn -join "/")
            }

            $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
            $arguments =
                if ($service) {
                    @("config", $Name, "binPath=", $BinaryPath, "start=", "auto", "DisplayName=", $DisplayName)
                }
                else {
                    @("create", $Name, "binPath=", $BinaryPath, "start=", "auto", "DisplayName=", $DisplayName)
                }

            if ($dependencySpec) {
                $arguments += @("depend=", $dependencySpec)
            }

            Invoke-ServiceCommand -Arguments $arguments
            Invoke-ServiceCommand -Arguments @("description", $Name, $Description)
            Invoke-ServiceCommand -Arguments @("failure", $Name, "reset= 86400", "actions= restart/60000/restart/60000/restart/60000")
        }

        function Wait-ServiceRunning {
            param([string]$Name)

            $service = Get-Service -Name $Name -ErrorAction Stop
            $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(60))
        }

        function Wait-HealthEndpoint {
            param([string]$HealthUrl)

            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            $lastError = $null

            while ([DateTime]::UtcNow -lt $deadline) {
                try {
                    return Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 10
                }
                catch {
                    $lastError = $_
                    Start-Sleep -Seconds 2
                }
            }

            throw "Health check failed for $HealthUrl. Last error: $($lastError.Exception.Message)"
        }

        $fsiHostExe = Join-Path $RemoteFsiHostDir "FSharp.MCP.DevKit.FsiHost.exe"
        $serverExe = Join-Path $RemoteServerDir "FSharp.MCP.DevKit.exe"

        if (-not (Test-Path -LiteralPath $fsiHostExe)) {
            throw "Remote fsihost executable not found: $fsiHostExe"
        }

        if (-not (Test-Path -LiteralPath $serverExe)) {
            throw "Remote server executable not found: $serverExe"
        }

        $fsiHostBinaryPath = '"' + $fsiHostExe + '" --service --service-name "' + $FsiHostServiceName + '"'
        $serverBinaryPath =
            '"' + $serverExe + '" --service-name "' + $ServerServiceName + '" --urls "' + $ServerUrls + '"'

        Ensure-ServiceConfigured `
            -Name $FsiHostServiceName `
            -DisplayName $FsiHostDisplayName `
            -BinaryPath $fsiHostBinaryPath `
            -DependsOn @() `
            -Description "net472 FSI session host for FSharp.MCP.DevKit"

        Ensure-ServiceConfigured `
            -Name $ServerServiceName `
            -DisplayName $ServerDisplayName `
            -BinaryPath $serverBinaryPath `
            -DependsOn @($FsiHostServiceName) `
            -Description "MCP server for FSharp.MCP.DevKit"

        if (-not $shouldSkipStart) {
            Start-Service -Name $FsiHostServiceName
            Wait-ServiceRunning -Name $FsiHostServiceName

            Start-Service -Name $ServerServiceName
            Wait-ServiceRunning -Name $ServerServiceName

            $healthResponse = Wait-HealthEndpoint -HealthUrl $RemoteHealthUrl
        }
        else {
            $healthResponse = [pscustomobject]@{ status = "start skipped" }
        }

        [pscustomobject]@{
            FsiHostService = $FsiHostServiceName
            ServerService = $ServerServiceName
            FsiHostPath = $RemoteFsiHostDir
            ServerPath = $RemoteServerDir
            HealthUrl = $RemoteHealthUrl
            HealthResponse = $healthResponse | ConvertTo-Json -Compress
        }
    } -ArgumentList $remoteFsiHostDir, $remoteServerDir, $FsiHostServiceName, $ServerServiceName, $FsiHostDisplayName, $ServerDisplayName, $serverUrls, $remoteHealthUrl, $skipStartFlag

    Write-Step "Deployment completed."
    $remoteSummary | Format-List | Out-Host
}
finally {
    if ($session) {
        Remove-PSSession -Session $session
    }

    if ($generatedLocalArtifacts -and (-not $KeepLocalArtifacts) -and (Test-Path -LiteralPath $localArtifactRoot)) {
        Remove-Item -LiteralPath $localArtifactRoot -Recurse -Force
    }
}
        $shouldSkipStart = [string]::Equals($SkipStartFlag, "true", [System.StringComparison]::OrdinalIgnoreCase)
