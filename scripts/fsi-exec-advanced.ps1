#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromPipeline = $true)]
    [string]$Code,

    [Parameter(Mandatory = $false)]
    [switch]$Detailed = $false,

    [Parameter(Mandatory = $false)]
    [string]$McpServerPath = ".",

    [Parameter(Mandatory = $false)]
    [switch]$ShowState = $false
)

Write-Error "scripts/fsi-exec-advanced.ps1 is a placeholder. It previously contained demo-only output and broken syntax; it still needs a real MCP client implementation."
exit 1
