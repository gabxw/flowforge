
[CmdletBinding(DefaultParameterSetName = 'New')]
param(
    [uri] $BaseUri = 'http://127.0.0.1:5080',
    [Parameter(ParameterSetName = 'New')] [switch] $PausedOnly,
    [Parameter(ParameterSetName = 'New')] [ValidateRange(1, 86400)] [int] $DelaySeconds = 45,
    [Parameter(Mandatory, ParameterSetName = 'Resume')] [Guid] $ExecutionId,
    [ValidateRange(1, 120)] [int] $TimeoutSeconds = 75
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
    return $response.Content | ConvertFrom-Json -Depth 20
}
function New-Execution([int] $Total, [int] $WaitSeconds) {
    $workflow = Invoke-Api POST '/api/workflows' @{ name = 'Condição transformação e espera'; description = 'Smoke com dados fictícios da Fase 9' } 201
    $path = "/api/workflows/$($workflow.id)"
    $ids = @(0..5 | ForEach-Object { [Guid]::NewGuid().ToString() })
    $workflow = Invoke-Api PUT "$path/draft" @{
        expectedRevision = $workflow.revision
        nodes = @(
            @{ nodeId = $ids[0]; configuration = @{ type = 'webhookTrigger' } }
            @{ nodeId = $ids[1]; configuration = @{ type = 'transformJson'; fields = @(
                @{ targetProperty = 'total'; literal = $Total }; @{ targetProperty = 'tag'; literal = 'ficticio-fase9' }
            ) } }
            @{ nodeId = $ids[2]; configuration = @{ type = 'condition'; sourcePointer = '/total'; operation = 'greaterThan'; expectedValue = 100 } }
            @{ nodeId = $ids[3]; configuration = @{ type = 'delay'; durationTicks = [TimeSpan]::FromSeconds($WaitSeconds).Ticks } }
            @{ nodeId = $ids[4]; configuration = @{ type = 'log'; message = 'Ramo falso' } }
            @{ nodeId = $ids[5]; configuration = @{ type = 'log'; message = 'Convergência' } }
        )
        connections = @(
            @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[0]; targetNodeId = $ids[1]; sourcePort = 'next' }
            @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[1]; targetNodeId = $ids[2]; sourcePort = 'next' }
            @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[2]; targetNodeId = $ids[3]; sourcePort = 'true' }
            @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[2]; targetNodeId = $ids[4]; sourcePort = 'false' }
            @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[3]; targetNodeId = $ids[5]; sourcePort = 'next' }
            @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[4]; targetNodeId = $ids[5]; sourcePort = 'next' }
        )
    }
    $workflow = Invoke-Api POST "$path/publish" @{ expectedRevision = $workflow.revision }
    return Invoke-Api POST "$path/executions" $null 202
}
function Wait-State([Guid] $Execution, [string] $Target) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = Invoke-Api GET "/api/executions/$Execution" $null
        if ($result.status -eq $Target -and ($Target -ne 'running' -or $result.resumeAt)) { return $result }
        if ($result.status -eq 'failed' -or $result.status -eq 'cancelled') { throw "Execução chegou a $($result.status) antes de $Target." }
        Start-Sleep -Milliseconds 150
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "Execução não chegou a $Target no limite."
}
function Archive-Workflow([Guid] $Workflow) {
    $path = "/api/workflows/$Workflow"; $current = Invoke-Api GET $path $null
    $null = Invoke-Api POST "$path/archive" @{ expectedRevision = $current.revision }
}
function Complete-Smoke([Guid] $Execution, [bool] $TrueBranch) {
    $result = Wait-State $Execution 'succeeded'
    if ($result.resumeAt -or -not $result.finishedAt) { throw 'Execução concluída reteve espera.' }
    $path = "/api/executions/$Execution"; $nodes = @(Invoke-Api GET "$path/nodes" $null); $logs = @(Invoke-Api GET "$path/logs" $null)
    if ($nodes.Count -ne 6 -or $logs.Count -ne $(if ($TrueBranch) { 1 } else { 2 })) { throw 'Histórico declarativo inconsistente.' }
    $skipped = $(if ($TrueBranch) { 4 } else { 3 })
    foreach ($i in 0..5) {
        if ($i -eq $skipped) {
            if ($nodes[$i].status -ne 'skipped' -or $nodes[$i].attemptCount -ne 0 -or $nodes[$i].startedAt) { throw 'Ramo não escolhido foi executado.' }
        } elseif ($nodes[$i].status -ne 'succeeded' -or $nodes[$i].attemptCount -ne 1) { throw 'Node escolhido não concluiu exatamente uma vez.' }
    }
    $expectedBytes = [Text.Encoding]::UTF8.GetByteCount((@{ total = $(if ($TrueBranch) { 150 } else { 50 }); tag = 'ficticio-fase9' } | ConvertTo-Json -Compress))
    if ($nodes[5].input.byteLength -ne $expectedBytes) { throw 'Contexto transformado não chegou à convergência.' }
    if (($nodes | ConvertTo-Json -Depth 12 -Compress) -match 'ficticio-fase9|executionContextProtected') { throw 'Contexto privado exposto.' }
    Archive-Workflow ([Guid]$result.workflowId)
    Write-Host "Condition, Transform e Delay aprovados ($Execution; ramo $TrueBranch)."
}
if ($PSCmdlet.ParameterSetName -eq 'Resume') { Complete-Smoke $ExecutionId $true; return }
if ($PausedOnly) {
    if ($DelaySeconds -lt 15) { throw 'Recriação exige uma espera de pelo menos 15 segundos.' }
    $execution = New-Execution 150 $DelaySeconds
    $waiting = Wait-State ([Guid]$execution.id) 'running'
    $nodes = @(Invoke-Api GET "/api/executions/$($execution.id)/nodes" $null)
    if ($nodes[3].status -ne 'running' -or $nodes[3].attemptCount -ne 1 -or $nodes[5].status -ne 'pending' -or $nodes[3].finishedAt) { throw 'Delay não suspendeu com histórico correto.' }
    Write-Host 'Delay suspenso para testar recriação do Worker.'
    return [pscustomobject]@{ ExecutionId = [Guid]$execution.id; ResumeAt = $waiting.resumeAt }
}
# Os dois caminhos e a convergência: a espera de 24h no ramo falso não pode ser iniciada.
$positive = New-Execution 150 1
Complete-Smoke ([Guid]$positive.id) $true
$negative = New-Execution 50 86400
Complete-Smoke ([Guid]$negative.id) $false
# Cancelamento acelera a mensagem já agendada, sem executar a engine na API.
$cancel = New-Execution 150 86400
$waiting = Wait-State ([Guid]$cancel.id) 'running'
$requested = Invoke-Api POST "/api/executions/$($cancel.id)/cancel" $null 202
if (-not $requested.cancelRequestedAt) { throw 'Cancelamento não foi solicitado.' }
$timer = [Diagnostics.Stopwatch]::StartNew()
do {
    $result = Invoke-Api GET "/api/executions/$($cancel.id)" $null
    if ($result.status -eq 'cancelled') { break }
    Start-Sleep -Milliseconds 150
} while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
if ($result.status -ne 'cancelled' -or $result.resumeAt) { throw 'Cancelamento aguardou o prazo de 24 horas.' }
$nodes = @(Invoke-Api GET "/api/executions/$($cancel.id)/nodes" $null)
$logs = @(Invoke-Api GET "/api/executions/$($cancel.id)/logs" $null)
if ($nodes[3].status -ne 'cancelled' -or $nodes[3].attemptCount -ne 1 -or $nodes[5].status -ne 'skipped' -or $logs.Count -ne 0) { throw 'Histórico do cancelamento incorreto.' }
Archive-Workflow ([Guid]$result.workflowId)
Write-Host 'Cancelamento de espera de 24 horas aprovado.'
