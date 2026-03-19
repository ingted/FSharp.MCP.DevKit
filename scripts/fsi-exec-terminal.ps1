#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Code,

    [Parameter(Mandatory = $true, Position = 1)]
    [int]$TerminalIndex,

    [Parameter(Mandatory = $false)]
    [switch]$Detailed = $false
)

Write-Error "scripts/fsi-exec-terminal.ps1 is a placeholder. Terminal-index routing is not implemented as a real MCP client wrapper."
exit 1
