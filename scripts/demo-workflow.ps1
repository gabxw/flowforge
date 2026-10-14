param(
    [string] $BaseUri = 'http://127.0.0.1:5173',
    [switch] $IncludeFailure,
    [int] $TimeoutSeconds = 45
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/auth-session.ps1"
function Invoke-DemoApi([string] $Method, [string] $Path, $Body = $null, [int] $Expected = 200) {
    $arguments = @{ Method = $Method; Uri = "$BaseUri$Path"; Headers = (Get-FlowForgeSmokeHeaders $BaseUri); TimeoutSec = 20; SkipHttpErrorCheck = $true }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 30 -Compress))
    }
    $response = Invoke-WebRequest @arguments
    if ($response.StatusCode -ne $Expected) { throw "Demonstração recebeu HTTP $($response.StatusCode); esperado $Expected em $Method $Path." }
    if ($response.Content) { return $response.Content | ConvertFrom-Json }
}
function Wait-DemoExecution([Guid] $Id, [string] $Expected) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $execution = Invoke-DemoApi GET "/api/executions/$Id"
        if ($execution.status -in @('succeeded', 'failed', 'cancelled')) {
            if ($execution.status -ne $Expected) { throw "Execução chegou a $($execution.status), esperado $Expected." }
            return $execution
        }
        Start-Sleep -Milliseconds 200
    } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw 'A demonstração não terminou no limite. Verifique Worker, outbox e deadline.'
}
function New-DemoWorkflow([string] $Name, $Nodes, $Connections) {
    for ($i = 0; $i -lt $Nodes.Count; $i++) {
        if (-not $Nodes[$i].ContainsKey('position')) { $Nodes[$i].position = @{ x = 240 * $i; y = 0 } }
    }
    $workflow = Invoke-DemoApi POST '/api/workflows' @{ name = $Name; description = 'Dados fictícios para demonstração do portfólio' } 201
    $workflow = Invoke-DemoApi PUT "/api/workflows/$($workflow.id)/draft" @{ expectedRevision = $workflow.revision; nodes = $Nodes; connections = $Connections }
    return Invoke-DemoApi POST "/api/workflows/$($workflow.id)/publish" @{ expectedRevision = $workflow.revision }
}
function New-DemoConnection([string] $Source, [string] $Target, [string] $Port = 'next') {
    return @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $Source; targetNodeId = $Target; sourcePort = $Port }
}
$ids = @(0..5 | ForEach-Object { [Guid]::NewGuid().ToString() })
$nodes = @(
    @{ nodeId = $ids[0]; configuration = @{ type = 'webhookTrigger' } }
    @{ nodeId = $ids[1]; configuration = @{ type = 'condition'; sourcePointer = '/total'; operation = 'greaterThan'; expectedValue = 100 } }
    @{ nodeId = $ids[2]; configuration = @{ type = 'transformJson'; fields = @(
        @{ targetProperty = 'total'; sourcePointer = '/total' }
        @{ targetProperty = 'origem'; literal = 'portfolio' }
    ) } }
    @{ nodeId = $ids[3]; configuration = @{ type = 'delay'; durationTicks = [TimeSpan]::FromSeconds(2).Ticks } }
    @{ nodeId = $ids[4]; configuration = @{ type = 'log'; message = 'Evento aprovado após transformação e espera' } }
    @{ nodeId = $ids[5]; configuration = @{ type = 'log'; message = 'Evento encerrado pelo ramo alternativo' } }
)
$connections = @(
    (New-DemoConnection $ids[0] $ids[1])
    (New-DemoConnection $ids[1] $ids[2] 'true')
    (New-DemoConnection $ids[2] $ids[3])
    (New-DemoConnection $ids[3] $ids[4])
    (New-DemoConnection $ids[1] $ids[5] 'false')
)
# Layout é dado do editor; não determina a ordem de execução.
$positions = @(@(0,120), @(240,120), @(480,0), @(720,0), @(960,0), @(480,260))
for ($i = 0; $i -lt $nodes.Count; $i++) { $nodes[$i].position = @{ x = $positions[$i][0]; y = $positions[$i][1] } }
$workflow = New-DemoWorkflow 'Pedidos de demonstração' $nodes $connections
$hook = Invoke-DemoApi POST "/api/workflows/$($workflow.id)/webhook" $null 201
function Send-DemoEvent([int] $Total, [string] $Key) {
    $response = Invoke-WebRequest -Method POST -Uri "$BaseUri/hooks/$($hook.endpoint.id)" -ContentType 'application/json' -TimeoutSec 20 -SkipHttpErrorCheck -Headers @{
        'X-FlowForge-Webhook-Secret' = $hook.secret; 'Idempotency-Key' = $Key
    } -Body ([Text.Encoding]::UTF8.GetBytes((@{ total = $Total; cliente = 'Pessoa fictícia' } | ConvertTo-Json -Compress)))
    if ($response.StatusCode -ne 202) { throw "Webhook respondeu HTTP $($response.StatusCode)." }
    return $response.Content | ConvertFrom-Json
}
$key = [Guid]::NewGuid().ToString()
$accepted = Send-DemoEvent 150 $key
$duplicate = Send-DemoEvent 150 $key
if ($duplicate.executionId -ne $accepted.executionId) { throw 'Idempotência do exemplo falhou.' }
$alternative = Send-DemoEvent 25 ([Guid]::NewGuid().ToString())
$null = Wait-DemoExecution $accepted.executionId 'succeeded'
$null = Wait-DemoExecution $alternative.executionId 'succeeded'
$history = @(Invoke-DemoApi GET "/api/executions/$($accepted.executionId)/nodes")
if ($history.Count -ne 6 -or @($history | Where-Object status -eq 'skipped').Count -ne 1) { throw 'Histórico do ramo aprovado inconsistente.' }
[pscustomobject]@{ WorkflowId = $workflow.id; TrueExecutionId = $accepted.executionId; FalseExecutionId = $alternative.executionId; IdempotencyVerified = $true }
if ($IncludeFailure) {
    $badIds = @(0..2 | ForEach-Object { [Guid]::NewGuid().ToString() })
    $bad = New-DemoWorkflow 'Diagnóstico de destino bloqueado' @(
        @{ nodeId = $badIds[0]; configuration = @{ type = 'webhookTrigger' } }
        @{ nodeId = $badIds[1]; configuration = @{ type = 'httpRequest'; url = 'https://flowforge-demo.invalid/blocked'; method = 'get'; maxAttempts = 1 } }
        @{ nodeId = $badIds[2]; configuration = @{ type = 'log'; message = 'Esta etapa deve ser pulada' } }
    ) @((New-DemoConnection $badIds[0] $badIds[1]), (New-DemoConnection $badIds[1] $badIds[2]))
    $failed = Invoke-DemoApi POST "/api/workflows/$($bad.id)/executions" $null 202
    $execution = Wait-DemoExecution $failed.id 'failed'
    if ($execution.errorCode -ne 'httpDestinationDenied') { throw 'Falha esperada de destino não confirmada.' }
    [pscustomobject]@{ WorkflowId = $bad.id; FailedExecutionId = $execution.id; ExpectedFailure = $execution.errorCode }
}
Write-Host 'Demonstração aprovada. Consulte workflows, nodes, logs e traces na interface. Secrets permanecem somente em memória.'
