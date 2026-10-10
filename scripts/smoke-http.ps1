[CmdletBinding(DefaultParameterSetName = 'New')]
param(
    [uri] $BaseUri = 'http://127.0.0.1:5080',
    [Parameter(ParameterSetName = 'New')] [switch] $PendingOnly,
    [Parameter(Mandatory, ParameterSetName = 'Resume')] [Guid] $ExecutionId,
    [Parameter(Mandatory, ParameterSetName = 'Resume')] [Guid] $CredentialId,
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
    return $response.Content | ConvertFrom-Json -Depth 20
}
function Complete-Smoke([Guid] $Execution, [Guid] $Credential) {
    $executionPath = "/api/executions/$Execution"; $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = Invoke-Api GET $executionPath $null
        if ($result.status -eq 'failed') { break }
        if ($result.status -notin @('pending', 'running')) { throw 'Resultado inesperado da política HTTP.' }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    if ($result.status -ne 'failed' -or $result.errorCode -ne 'httpDestinationDenied') { throw 'O Worker não recusou o destino sem autorização.' }
    $nodes = @(Invoke-Api GET "$executionPath/nodes" $null); $logs = @(Invoke-Api GET "$executionPath/logs" $null)
    if ($nodes.Count -ne 3 -or $logs.Count -ne 0 -or $nodes[0].status -ne 'succeeded' -or $nodes[1].status -ne 'failed' -or
        $nodes[1].errorCode -ne 'httpDestinationDenied' -or $nodes[1].attemptCount -ne 1 -or $nodes[2].status -ne 'skipped') { throw 'Histórico HTTP inconsistente.' }
    if (($nodes | ConvertTo-Json -Depth 10 -Compress) -match 'protectedValue|executionContextProtected|authorization|bearer') { throw 'Histórico expôs detalhes de autenticação.' }
    $credentialPath = "/api/credentials/$Credential"; $metadata = Invoke-Api GET $credentialPath $null
    if ($metadata.revision -ne 2 -or $metadata.revokedAt) { throw 'Metadados da credencial não sobreviveram.' }
    $revoked = Invoke-Api POST "$credentialPath/revoke" @{ expectedRevision = 2 }
    if ($revoked.revision -ne 3 -or -not $revoked.revokedAt) { throw 'Revogação não foi registrada.' }
    $workflowPath = "/api/workflows/$($result.workflowId)"; $workflow = Invoke-Api GET $workflowPath $null
    $null = Invoke-Api POST "$workflowPath/archive" @{ expectedRevision = $workflow.revision }
    Write-Host "Credenciais e HTTP aprovados: metadados persistidos, destino recusado e histórico protegido ($Execution)."
}
if ($PSCmdlet.ParameterSetName -eq 'Resume') { Complete-Smoke $ExecutionId $CredentialId; return }
# Dados fictícios. O destino reservado .test deve permanecer fora da allowlist do operador.
$secret = 'ficticio-' + [Guid]::NewGuid().ToString('N'); $next = 'ficticio-' + [Guid]::NewGuid().ToString('N')
$credential = Invoke-Api POST '/api/credentials' @{ name = 'Credencial fictícia do smoke'; type = 'bearerToken'; origin = 'https://blocked.flowforge.test'; secret = $secret } 201
$credentialPath = "/api/credentials/$($credential.id)"
$metadata = Invoke-Api GET $credentialPath $null
if (($metadata | ConvertTo-Json -Depth 10 -Compress).Contains($secret) -or $metadata.PSObject.Properties.Name -contains 'protectedValue') { throw 'Credencial expôs seu valor.' }
$credential = Invoke-Api POST "$credentialPath/rotate-secret" @{ expectedRevision = 1; secret = $next }
if ($credential.revision -ne 2) { throw 'Rotação não avançou a revisão.' }
$null = Invoke-Api POST "$credentialPath/rotate-secret" @{ expectedRevision = 1; secret = $secret } 409
$workflow = Invoke-Api POST '/api/workflows' @{ name = 'HTTP da Fase 8'; description = 'HTTP bloqueado por política, sem acesso à rede' } 201
$path = "/api/workflows/$($workflow.id)"; $trigger = [Guid]::NewGuid().ToString(); $http = [Guid]::NewGuid().ToString(); $log = [Guid]::NewGuid().ToString()
$workflow = Invoke-Api PUT "$path/draft" @{
    expectedRevision = $workflow.revision
    nodes = @(
        @{ nodeId = $trigger; configuration = @{ type = 'webhookTrigger' } }
        @{ nodeId = $http; credentialId = $credential.id; configuration = @{ type = 'httpRequest'; url = 'https://blocked.flowforge.test/never'; method = 'post' } }
        @{ nodeId = $log; configuration = @{ type = 'log'; message = 'Não deve executar' } }
    )
    connections = @(
        @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $trigger; targetNodeId = $http; sourcePort = 'next' }
        @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $http; targetNodeId = $log; sourcePort = 'next' }
    )
}
$workflow = Invoke-Api POST "$path/publish" @{ expectedRevision = $workflow.revision }
$execution = Invoke-Api POST "$path/executions" $null 202
if ($PendingOnly) {
    $pending = Invoke-Api GET "/api/executions/$($execution.id)" $null
    if ($pending.status -ne 'pending' -or $pending.startedAt) { throw 'A execução HTTP deveria aguardar o broker.' }
    Write-Host 'Execução HTTP com credencial aceita para validar recriação dos processos.'
    return [pscustomobject]@{ ExecutionId = [Guid]$execution.id; CredentialId = [Guid]$credential.id }
}
Complete-Smoke ([Guid]$execution.id) ([Guid]$credential.id)
