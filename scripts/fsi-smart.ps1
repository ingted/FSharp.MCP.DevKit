#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Code,

    [Parameter(Mandatory = $false)]
    [switch]$Detailed = $false,

    [Parameter(Mandatory = $false)]
    [switch]$UseCurrent = $false,

    [Parameter(Mandatory = $false)]
    [string]$Target = "default"
)

Write-Error "scripts/fsi-smart.ps1 is a placeholder. It does not invoke the merged MCP server. Use a real MCP client or replace this script with an actual client wrapper."
exit 1
