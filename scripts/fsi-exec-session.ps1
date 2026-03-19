#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Code,

    [Parameter(Mandatory = $true, Position = 1)]
    [string]$SessionName,

    [Parameter(Mandatory = $false)]
    [switch]$Detailed = $false
)

Write-Error "scripts/fsi-exec-session.ps1 is a placeholder. Named-session routing is not implemented as a real MCP client wrapper."
exit 1
