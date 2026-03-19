# F# MCP DevKit PowerShell Aliases
# Add these to your PowerShell profile or source this file

# Simple F# execution
function Invoke-FSharpCode {
    param([string]$Code)
    & "$PSScriptRoot\fsi-exec-advanced.ps1" -Code $Code
}

# Detailed F# execution with error reporting
function Invoke-FSharpCodeDetailed {
    param([string]$Code)
    & "$PSScriptRoot\fsi-exec-advanced.ps1" -Code $Code -Detailed
}

# F# execution with state check
function Invoke-FSharpCodeWithState {
    param([string]$Code)
    & "$PSScriptRoot\fsi-exec-advanced.ps1" -Code $Code -ShowState
}

# Create convenient aliases
Set-Alias -Name "fs" -Value "Invoke-FSharpCode"
Set-Alias -Name "fsd" -Value "Invoke-FSharpCodeDetailed"
Set-Alias -Name "fss" -Value "Invoke-FSharpCodeWithState"

Write-Host "F# MCP DevKit aliases loaded." -ForegroundColor Green
Write-Host "Underlying fsi-* scripts are placeholders until a real MCP client wrapper is implemented." -ForegroundColor Yellow
