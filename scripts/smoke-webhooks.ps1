[CmdletBinding(DefaultParameterSetName = 'New')]
param(
    [uri] $BaseUri = 'http://127.0.0.1:5080',
    [Parameter(ParameterSetName = 'New')] [switch] $PendingOnly,
    [Parameter(Mandatory, ParameterSetName = 'Resume')] [Guid] $ExecutionId,
    [Parameter(Mandatory, ParameterSetName = 'Resume')] [ValidateRange(1, 65536)] [int] $ExpectedInputBytes,
    [ValidateRange(1, 120)] [int] $TimeoutSeconds = 45
)
$ErrorActionPreference = 'Stop'
$base = $BaseUri.AbsoluteUri.TrimEnd('/')
function Invoke-Api([string] $Method, [string] $Path, [object] $Body, [int] $Expected = 200) {
    $parameters = @{ Uri = $base + $Path; Method = $Method; TimeoutSec = 15; SkipHttpErrorCheck = $true }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 20 -Compress))
    }
    $response = Invoke-WebRequest @parameters
    if ($response.StatusCode -ne $Expected) { throw "HTTP $Method $($Path): $($response.StatusCode); esperado $Expected." }
    return $response.Content | ConvertFrom-Json -Depth 20
}
function Invoke-Hook([string] $Hook, [string] $Secret, [byte[]] $Payload, [string] $Key, [int] $Expected = 202) {
    $parameters = @{ Uri = $base + $Hook; Method = 'POST'; TimeoutSec = 15; SkipHttpErrorCheck = $true
        ContentType = 'application/json; charset=utf-8'; Body = $Payload; Headers = @{ 'X-FlowForge-Webhook-Secret' = $Secret; 'Idempotency-Key' = $Key } }
    $response = Invoke-WebRequest @parameters
    if ($response.StatusCode -ne $Expected) { throw "Webhook: HTTP $($response.StatusCode); esperado $Expected." }
    return $response.Content | ConvertFrom-Json -Depth 20
}
function Wait-Execution([Guid] $Id, [int] $InputBytes) {
    $path = "/api/executions/$Id"; $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $execution = Invoke-Api GET $path $null
        if ($execution.status -eq 'succeeded') { break }
        if ($execution.status -notin @('pending', 'running')) { throw 'Resultado inesperado do webhook.' }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    if ($execution.status -ne 'succeeded') { throw 'O Worker não concluiu o webhook.' }
    $nodes = @(Invoke-Api GET "$path/nodes" $null); $logs = @(Invoke-Api GET "$path/logs" $null)
    if ($nodes.Count -ne 2 -or $logs.Count -ne 1) { throw 'Histórico incompleto.' }
    foreach ($node in $nodes) {
        if ($node.status -ne 'succeeded' -or $node.attemptCount -ne 1 -or $node.input.byteLength -ne $InputBytes -or $node.output.byteLength -ne $InputBytes) {
            throw 'O payload não foi propagado corretamente ao histórico.'
        }
    }
    $history = @{ nodes = $nodes; logs = $logs } | ConvertTo-Json -Depth 20 -Compress
    if ($history -match 'protected|privateToken|ownerUserId') { throw 'Histórico expôs conteúdo privado.' }
    $workflowPath = "/api/workflows/$($execution.workflowId)"
    $workflow = Invoke-Api GET $workflowPath $null
    $null = Invoke-Api POST "$workflowPath/archive" @{ expectedRevision = $workflow.revision }
    Write-Host "Webhook aprovado: payload durável, Trigger → Log e histórico protegido ($Id)."
}
if ($PSCmdlet.ParameterSetName -eq 'Resume') {
    Wait-Execution $ExecutionId $ExpectedInputBytes
    return
}
$workflow = Invoke-Api POST '/api/workflows' @{ name = 'Webhook da Fase 7'; description = 'Input protegido → Trigger → Log' } 201
$path = "/api/workflows/$($workflow.id)"; $trigger = [Guid]::NewGuid().ToString(); $log = [Guid]::NewGuid().ToString()
$workflow = Invoke-Api PUT "$path/draft" @{
    expectedRevision = $workflow.revision
    nodes = @(@{ nodeId = $trigger; configuration = @{ type = 'webhookTrigger' } }, @{ nodeId = $log; configuration = @{ type = 'log'; message = 'Evento recebido' } })
    connections = @(@{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $trigger; targetNodeId = $log; sourcePort = 'next' })
}
$workflow = Invoke-Api POST "$path/publish" @{ expectedRevision = $workflow.revision }
$issued = Invoke-Api POST "$path/webhook" $null 201
$hook = "/hooks/$($issued.endpoint.id)"; $secret = $issued.secret; $key = [Guid]::NewGuid().ToString('N')
$payload = [Text.Encoding]::UTF8.GetBytes((@{ event = 42; privateToken = 'dado-ficticio-do-smoke' } | ConvertTo-Json -Compress))
$receipt = Invoke-Hook $hook $secret $payload $key
if ($receipt.replayed -or $receipt.workflowVersionId -ne $workflow.currentPublishedVersionId) { throw 'Aceite inconsistente.' }
if ($PendingOnly) {
    $pending = Invoke-Api GET "/api/executions/$($receipt.executionId)" $null
    if ($pending.status -ne 'pending' -or $pending.startedAt) { throw 'A execução deveria aguardar o broker.' }
    Write-Host 'Webhook aceito com broker parado; payload e outbox aguardam retomada.'
    return [pscustomobject]@{ ExecutionId = $receipt.executionId; ExpectedInputBytes = $payload.Length }
}
$duplicate = Invoke-Hook $hook $secret $payload $key
if (-not $duplicate.replayed -or $duplicate.executionId -ne $receipt.executionId) { throw 'Deduplicação do webhook falhou.' }
$null = Invoke-Hook $hook $secret ([Text.Encoding]::UTF8.GetBytes('{"event":99}')) $key 409
$null = Invoke-Hook $hook ('A' * 64) $payload $key 404
$metadata = Invoke-Api GET "$path/webhook" $null
if ($metadata.PSObject.Properties.Name -match 'secret|ownerUserId') { throw 'Metadados expuseram segredo ou proprietário.' }
$null = Invoke-Api PUT "$path/webhook" @{ enabled = $false }
$null = Invoke-Hook $hook $secret $payload $key 404
$rotated = Invoke-Api POST "$path/webhook/rotate-secret" $null
if ($rotated.endpoint.enabled -or $rotated.secret -eq $secret) { throw 'Rotação alterou habilitação ou preservou segredo.' }
$null = Invoke-Api PUT "$path/webhook" @{ enabled = $true }
$null = Invoke-Hook $hook $secret $payload $key 404
$afterRotation = Invoke-Hook $hook $rotated.secret $payload $key
if ($afterRotation.executionId -ne $receipt.executionId) { throw 'Rotação descartou a reserva de idempotência.' }
Wait-Execution ([Guid]$receipt.executionId) $payload.Length
