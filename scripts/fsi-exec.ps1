#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Code,

    [Parameter(Mandatory = $false)]
    [switch]$Detailed = $false,

    [Parameter(Mandatory = $false)]
    [string]$TerminalId = "default",

    [Parameter(Mandatory = $false)]
    [string]$SessionName = "",

    [Parameter(Mandatory = $false)]
    [int]$TerminalIndex = -1
)

Write-Error "scripts/fsi-exec.ps1 is a placeholder. Use a real MCP client against /mcp or implement a client wrapper before relying on this script."
exit 1
