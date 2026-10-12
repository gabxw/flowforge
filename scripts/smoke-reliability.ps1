
[CmdletBinding(DefaultParameterSetName = 'New')]
param(
    [uri] $BaseUri = 'http://127.0.0.1:5080',
    [Parameter(ParameterSetName = 'New')] [switch] $PendingOnly,
    [Parameter(Mandatory, ParameterSetName = 'Resume')] [Guid] $ExecutionId,
    [ValidateRange(1, 120)] [int] $TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
$base = $BaseUri.AbsoluteUri.TrimEnd('/')
function Invoke-Api([string] $Method, [string] $Path, [object] $Body, [int] $Expected = 200, [string] $Key) {
    $parameters = @{ Uri = $base + $Path; Method = $Method; TimeoutSec = 10; SkipHttpErrorCheck = $true }
    if ($Key) { $parameters.Headers = @{ 'Idempotency-Key' = $Key } }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 20 -Compress))
    }
    $response = Invoke-WebRequest @parameters
    if ($response.StatusCode -ne $Expected) { throw "HTTP $Method $($Path): $($response.StatusCode); esperado $Expected." }
    return $response.Content | ConvertFrom-Json -Depth 20
}
function Wait-Failure([Guid] $Execution) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = Invoke-Api GET "/api/executions/$Execution" $null
        if ($result.status -eq 'failed') { break }
        if ($result.status -notin @('pending', 'running')) { throw 'Resultado de execução inesperado.' }
        Start-Sleep -Milliseconds 150
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    if ($result.status -ne 'failed' -or $result.errorCode -ne 'httpDestinationDenied' -or $result.resumeAt) { throw 'Falha permanente deveria encerrar sem retry.' }
    $path = "/api/executions/$Execution"; $attempts = @(Invoke-Api GET "$path/attempts" $null)
    if ($attempts.Count -ne 3 -or $attempts[2].status -ne 'failed' -or $attempts[2].externalOutcome -ne 'notStarted' -or
        $attempts[2].number -ne 1 -or $attempts[2].dispatchedAt) { throw 'Journal da tentativa HTTP inconsistente.' }
    if (($attempts | ConvertTo-Json -Depth 15 -Compress) -match 'protected|keyDigest|authorization') { throw 'Histórico expôs campos operacionais.' }
    return $result
}
function Complete-Smoke([Guid] $Execution) {
    $result = Wait-Failure $Execution; $path = "/api/executions/$Execution"
    $null = Invoke-Api POST "$path/replay" @{ acknowledgeExternalEffects = $true } 400
    $key = 'ficticio-replay-' + [Guid]::NewGuid().ToString('N')
    $null = Invoke-Api POST "$path/replay" @{ acknowledgeExternalEffects = $false } 409 $key
    $replay = Invoke-Api POST "$path/replay" @{ acknowledgeExternalEffects = $true } 202 $key
    $again = Invoke-Api POST "$path/replay" @{ acknowledgeExternalEffects = $true } 202 $key
    if ($replay.id -ne $again.id -or $replay.id -eq $result.id -or $replay.workflowVersionId -ne $result.workflowVersionId) { throw 'Replay não preservou idempotência/versão.' }
    $null = Wait-Failure ([Guid]$replay.id)
    $audits = @(Invoke-Api GET "$path/replays" $null)
    if ($audits.Count -ne 1 -or $audits[0].newExecutionId -ne $replay.id) { throw 'Auditoria de replay inconsistente.' }
    $old = Invoke-Api GET $path $null
    if ($old.finishedAt -ne $result.finishedAt -or $old.status -ne 'failed') { throw 'Replay alterou execução anterior.' }
    $workflowPath = "/api/workflows/$($result.workflowId)"; $workflow = Invoke-Api GET $workflowPath $null
    $null = Invoke-Api POST "$workflowPath/archive" @{ expectedRevision = $workflow.revision }
    Write-Host "Confiabilidade aprovada: deadline, falha permanente, tentativas e replay explícito ($Execution)."
}
if ($PSCmdlet.ParameterSetName -eq 'Resume') { Complete-Smoke $ExecutionId; return }
$workflow = Invoke-Api POST '/api/workflows' @{ name = 'Confiabilidade da Fase 10'; description = 'Dados fictícios; nenhuma chamada externa autorizada' } 201
$path = "/api/workflows/$($workflow.id)"; $ids = @(0..3 | ForEach-Object { [Guid]::NewGuid().ToString() })
$workflow = Invoke-Api PUT "$path/draft" @{
    expectedRevision = $workflow.revision
    nodes = @(
        @{ nodeId = $ids[0]; configuration = @{ type = 'webhookTrigger' } }
        @{ nodeId = $ids[1]; configuration = @{ type = 'condition'; sourcePointer = ''; operation = 'exists' } }
        @{ nodeId = $ids[2]; configuration = @{ type = 'httpRequest'; method = 'get'; url = 'https://blocked.flowforge.test'; maxAttempts = 3 } }
        @{ nodeId = $ids[3]; configuration = @{ type = 'log'; message = 'Não deve executar' } }
    )
    connections = @(
        @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[0]; targetNodeId = $ids[1]; sourcePort = 'next' }
        @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[1]; targetNodeId = $ids[2]; sourcePort = 'true' }
        @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[1]; targetNodeId = $ids[3]; sourcePort = 'false' }
        @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[2]; targetNodeId = $ids[3]; sourcePort = 'next' }
    )
}
$workflow = Invoke-Api POST "$path/publish" @{ expectedRevision = $workflow.revision }
$execution = Invoke-Api POST "$path/executions" $null 202
if (-not $execution.deadlineAt -or ([DateTimeOffset]$execution.deadlineAt - [DateTimeOffset]$execution.createdAt).TotalHours -lt 47) { throw 'Deadline da execução ausente.' }
if ($PendingOnly) {
    $pending = Invoke-Api GET "/api/executions/$($execution.id)" $null
    if ($pending.status -ne 'pending') { throw 'Pare o Worker antes do cenário de redrive.' }
    $dispatches = @(Invoke-Api GET "/api/executions/$($execution.id)/dispatches" $null)
    $body = @{ messageId = $dispatches[0].messageId; expectedPublishAttempts = $dispatches[0].publishAttempts }
    $audit = Invoke-Api POST "/api/executions/$($execution.id)/redrive" $body 202
    $duplicate = Invoke-Api POST "/api/executions/$($execution.id)/redrive" $body 202
    if ($audit.id -ne $duplicate.id) { throw 'Redrive duplicou a auditoria.' }
    Write-Host 'Redrive pendente aprovado; pronto para retomar com Worker recriado.'
    return [pscustomobject]@{ ExecutionId = [Guid]$execution.id }
}
Complete-Smoke ([Guid]$execution.id)
