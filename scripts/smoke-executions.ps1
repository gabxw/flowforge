[CmdletBinding()]
param(
    [uri] $BaseUri = 'http://127.0.0.1:5080',
    [ValidateRange(1, 120)] [int] $TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$base = $BaseUri.AbsoluteUri.TrimEnd('/')
function Invoke-Api([string] $Method, [string] $Path, [object] $Body, [int] $Expected = 200) {
    $parameters = @{ Uri = $base + $Path; Method = $Method; TimeoutSec = 10; SkipHttpErrorCheck = $true }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 20 -Compress))
    }
    $response = Invoke-WebRequest @parameters
    if ($response.StatusCode -ne $Expected) { throw "HTTP $Method $Path: $($response.StatusCode); esperado $Expected." }
    $json = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { $response.Content }
    return $json | ConvertFrom-Json -Depth 20
}

$workflow = Invoke-Api POST '/api/workflows' @{ name = 'Despacho da Fase 5'; description = 'Sem execução de nodes' } 201
$path = "/api/workflows/$($workflow.id)"
$triggerId = [Guid]::NewGuid().ToString()
$workflow = Invoke-Api PUT "$path/draft" @{
    expectedRevision = $workflow.revision
    nodes = @(@{ nodeId = $triggerId; configuration = @{ type = 'webhookTrigger' } })
    connections = @()
}
$workflow = Invoke-Api POST "$path/publish" @{ expectedRevision = $workflow.revision }
$execution = Invoke-Api POST "$path/executions" $null 202
if ($execution.status -ne 'pending' -or $execution.workflowVersionId -ne $workflow.currentPublishedVersionId) {
    throw 'O aceite não registrou Pending na publicação vigente.'
}
$executionPath = "/api/executions/$($execution.id)"
$timer = [Diagnostics.Stopwatch]::StartNew()
do {
    $result = Invoke-Api GET $executionPath $null
    if ($result.status -eq 'failed') { break }
    if ($result.status -notin @('pending', 'running')) { throw 'Resultado inesperado do despacho.' }
    Start-Sleep -Milliseconds 250
} while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
if ($result.status -ne 'failed' -or $result.errorCode -ne 'engineUnavailable' -or
    -not $result.startedAt -or -not $result.finishedAt) {
    throw 'O Worker não registrou a falha explícita da engine ainda indisponível.'
}
$null = Invoke-Api POST "$path/archive" @{ expectedRevision = $workflow.revision }
Write-Host "Despacho aprovado: API → outbox → RabbitMQ → Worker → resultado ($($execution.id)). Nenhum node foi executado."
