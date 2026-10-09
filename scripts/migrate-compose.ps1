[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9_-]*$')]
    [string] $ProjectName,
    [switch] $GenerateOnly
)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$sqlPath = Join-Path $taskRoot '.local/migrations.sql'
$previousConnection = [Environment]::GetEnvironmentVariable('FLOWFORGE_POSTGRES_CONNECTION_STRING', 'Process')

function Invoke-Check([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Falha em $Executable (código $LASTEXITCODE)." }
}

Push-Location $taskRoot
try {
    New-Item -ItemType Directory -Force (Split-Path $sqlPath -Parent) | Out-Null
    # A geração não conecta ao banco e não precisa ler a senha operacional.
    $env:FLOWFORGE_POSTGRES_CONNECTION_STRING = 'Host=127.0.0.1;Database=flowforge_design'
    Invoke-Check dotnet @('tool', 'restore')
    Invoke-Check dotnet @('restore', 'src/FlowForge.Infrastructure', '--locked-mode')
    Invoke-Check dotnet @('build', 'src/FlowForge.Infrastructure', '-c', 'Release', '--no-restore')
    Invoke-Check dotnet @('ef', 'migrations', 'has-pending-model-changes', '--project', 'src/FlowForge.Infrastructure',
        '--startup-project', 'src/FlowForge.Infrastructure', '--configuration', 'Release', '--no-build')
    Invoke-Check dotnet @('ef', 'migrations', 'script', '--idempotent', '--project', 'src/FlowForge.Infrastructure',
        '--startup-project', 'src/FlowForge.Infrastructure', '--configuration', 'Release', '--no-build', '--output', $sqlPath)
    if ($GenerateOnly) {
        Write-Host 'SQL idempotente gerado em .local/migrations.sql. Nenhum banco alterado.'
        return
    }
    $composeArguments = @('compose')
    if ($ProjectName) { $composeArguments += @('-p', $ProjectName) }
    # Socket local no próprio PostgreSQL; segredo não vai em argumentos nem em logs.
    $composeArguments += @('exec', '-T', 'postgres', 'psql', '-v', 'ON_ERROR_STOP=1', '-U', 'flowforge', '-d', 'flowforge')
    Get-Content -LiteralPath $sqlPath -Raw | & docker @composeArguments
    if ($LASTEXITCODE -ne 0) { throw "Falha ao aplicar migrations (código $LASTEXITCODE)." }
    Write-Host 'Migrations aplicadas ao PostgreSQL deste projeto Compose.'
} finally {
    [Environment]::SetEnvironmentVariable('FLOWFORGE_POSTGRES_CONNECTION_STRING', $previousConnection, 'Process')
    Pop-Location
}
