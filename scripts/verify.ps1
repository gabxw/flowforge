[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent

function Invoke-Check {
    param([string] $Executable, [string[]] $Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Check failed: $Executable $($Arguments -join ' ') (exit $LASTEXITCODE)"
    }
}

Push-Location $taskRoot
try {
    Invoke-Check 'dotnet' @('restore', 'FlowForge.slnx')
    Invoke-Check 'dotnet' @('build', 'FlowForge.slnx', '-c', 'Release', '--no-restore')
    Invoke-Check 'dotnet' @('test', 'FlowForge.slnx', '-c', 'Release', '--no-build')

    Push-Location (Join-Path $taskRoot 'frontend')
    try {
        if (Test-Path -LiteralPath 'package-lock.json') {
            Invoke-Check 'npm.cmd' @('ci')
        } else {
            Invoke-Check 'npm.cmd' @('install')
        }
        Invoke-Check 'npm.cmd' @('run', 'lint')
        Invoke-Check 'npm.cmd' @('run', 'build')
    } finally {
        Pop-Location
    }

    Invoke-Check 'docker' @('compose', 'config', '--quiet')
    Write-Host 'Build, tests, frontend and Compose configuration checks passed.'
    Write-Host 'Run docker compose up, then scripts/smoke-compose.ps1 to verify containers.'
} finally {
    Pop-Location
}
