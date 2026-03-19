#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory = $false)]
    [switch]$ListSessions = $false,

    [Parameter(Mandatory = $false)]
    [switch]$ListTerminals = $false,

    [Parameter(Mandatory = $false)]
    [switch]$ShowActive = $false
)

Write-Error "scripts/fsi-discover.ps1 is a placeholder. Session and terminal discovery are not implemented in a real MCP client path yet."
exit 1
