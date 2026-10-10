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
    if ($response.StatusCode -ne $Expected) { throw "HTTP $Method $($Path): $($response.StatusCode); esperado $Expected." }
    $json = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { $response.Content }
    return $json | ConvertFrom-Json -Depth 20
}

# Comando privado/manual: input inicial {}, sem autenticação pública ou payload webhook.
$workflow = Invoke-Api POST '/api/workflows' @{ name = 'Engine da Fase 6'; description = 'Trigger → Log' } 201
$path = "/api/workflows/$($workflow.id)"
$triggerId = [Guid]::NewGuid().ToString()
$logId = [Guid]::NewGuid().ToString()
$message = 'Mensagem fictícia do smoke'
$workflow = Invoke-Api PUT "$path/draft" @{
    expectedRevision = $workflow.revision
    nodes = @(
        @{ nodeId = $triggerId; configuration = @{ type = 'webhookTrigger' } }
        @{ nodeId = $logId; configuration = @{ type = 'log'; message = $message } }
    )
    connections = @(@{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $triggerId; targetNodeId = $logId; sourcePort = 'next' })
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
    if ($result.status -eq 'succeeded') { break }
    if ($result.status -notin @('pending', 'running')) { throw 'Resultado inesperado da engine.' }
    Start-Sleep -Milliseconds 250
} while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
if ($result.status -ne 'succeeded' -or $result.errorCode -or -not $result.startedAt -or -not $result.finishedAt) {
    throw 'O Worker não concluiu o workflow simples.'
}
$nodes = @(Invoke-Api GET "$executionPath/nodes" $null)
$logs = @(Invoke-Api GET "$executionPath/logs" $null)
if ($nodes.Count -ne 2 -or $logs.Count -ne 1 -or $logs[0].eventCode -ne 'logRecorded') { throw 'Histórico incompleto.' }
foreach ($node in $nodes) {
    if ($node.status -ne 'succeeded' -or $node.attemptCount -ne 1 -or -not $node.startedAt -or -not $node.finishedAt -or
        $node.input.byteLength -ne 2 -or $node.output.byteLength -ne 2) { throw 'NodeExecution inconsistente.' }
}
$publicHistory = @{ nodes = $nodes; logs = $logs } | ConvertTo-Json -Depth 20 -Compress
if ($publicHistory.Contains($message) -or $publicHistory -match 'executionContextProtected|messageProtected|ownerUserId') {
    throw 'O histórico expôs conteúdo protegido.'
}
$terminal = Invoke-Api POST "$executionPath/cancel" $null 200
if ($terminal.status -ne 'succeeded' -or $terminal.cancelRequestedAt) { throw 'Cancelamento alterou resultado terminal.' }
$null = Invoke-Api POST "$path/archive" @{ expectedRevision = $workflow.revision }
Write-Host "Engine aprovada: Trigger → Log, histórico protegido e estado terminal ($($execution.id))."
