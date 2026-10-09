[CmdletBinding()]
param([uri] $BaseUri = 'http://127.0.0.1:5080')

$ErrorActionPreference = 'Stop'
$base = $BaseUri.AbsoluteUri.TrimEnd('/')

function Invoke-Api([string] $Method, [string] $Path, [object] $Body, [int] $Expected = 200) {
    $arguments = @{
        Uri = $base + $Path
        Method = $Method
        TimeoutSec = 20
        SkipHttpErrorCheck = $true
    }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json; charset=utf-8'
        $arguments.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 20 -Compress))
    }
    $response = Invoke-WebRequest @arguments
    if ($response.StatusCode -ne $Expected) {
        throw "HTTP $Method $Path retornou $($response.StatusCode); esperado $Expected."
    }
    if ($Expected -ge 400 -and [string] $response.Headers['Content-Type'] -notlike 'application/problem+json*') {
        throw 'A resposta de erro não usa Problem Details.'
    }
    Write-Host "PASS $Method $Path ($Expected)"
    # Algumas versões do PowerShell entregam application/problem+json como bytes.
    $json = if ($response.Content -is [byte[]]) {
        [Text.Encoding]::UTF8.GetString($response.Content)
    } else { $response.Content }
    return $json | ConvertFrom-Json -Depth 30
}

$workflow = Invoke-Api POST '/api/workflows' @{ name = 'Demonstração da Fase 4'; description = 'Definição sem execução de nodes' } 201
$path = "/api/workflows/$($workflow.id)"
$initialRevision = $workflow.revision
$invalid = Invoke-Api POST "$path/publish" @{ expectedRevision = $initialRevision } 422
if ('emptyGraph' -notin $invalid.errors.code) { throw 'Publicação vazia não explicou a regra violada.' }
$workflow = Invoke-Api PUT $path @{ expectedRevision = $initialRevision; name = 'Exemplo publicado'; description = 'Seis tipos de node' }
$ids = 1..6 | ForEach-Object { [Guid]::NewGuid().ToString() }
$nodes = @(
    @{ nodeId = $ids[0]; configuration = @{ type = 'webhookTrigger' }; position = @{ x = 0; y = 0 } },
    @{ nodeId = $ids[1]; configuration = @{ type = 'condition'; sourcePointer = '/total'; operation = 'greaterThan'; expectedValue = 100 } },
    @{ nodeId = $ids[2]; configuration = @{ type = 'httpRequest'; url = 'https://example.com/resource'; method = 'get' } },
    @{ nodeId = $ids[3]; configuration = @{ type = 'transformJson'; fields = @(
        @{ targetProperty = 'result'; sourcePointer = '' },
        @{ targetProperty = 'optional'; literal = $null }
    ) } },
    @{ nodeId = $ids[4]; configuration = @{ type = 'delay'; durationTicks = 50000000 } },
    @{ nodeId = $ids[5]; configuration = @{ type = 'log'; message = 'Definição validada' } }
)
function Edge([int] $Source, [int] $Target, [string] $Port) {
    return @{ id = [Guid]::NewGuid().ToString(); sourceNodeId = $ids[$Source]; targetNodeId = $ids[$Target]; sourcePort = $Port }
}
$connections = @((Edge 0 1 'next'), (Edge 1 2 'true'), (Edge 1 5 'false'),
    (Edge 2 3 'next'), (Edge 3 4 'next'), (Edge 4 5 'next'))
$beforeSave = $workflow.revision
$workflow = Invoke-Api PUT "$path/draft" @{ expectedRevision = $beforeSave; nodes = $nodes; connections = $connections }
$null = Invoke-Api PUT $path @{ expectedRevision = $beforeSave; name = 'Edição obsoleta' } 409
$workflow = Invoke-Api POST "$path/publish" @{ expectedRevision = $workflow.revision }
$publishedId = $workflow.currentPublishedVersionId
$version = Invoke-Api GET "$path/versions/$publishedId" $null
if ($version.status -ne 'published' -or $version.nodes.Count -ne 6) { throw 'A versão publicada não preservou o grafo.' }
$workflow = Invoke-Api POST "$path/drafts" @{ expectedRevision = $workflow.revision } 201
if ($workflow.versions.Count -ne 2 -or $workflow.currentPublishedVersionId -ne $publishedId) {
    throw 'Criar rascunho alterou a publicação vigente.'
}
$workflow = Invoke-Api POST "$path/archive" @{ expectedRevision = $workflow.revision }
if (-not $workflow.archivedAt) { throw 'O arquivamento não foi persistido.' }
$page = Invoke-Api GET '/api/workflows?offset=0&limit=100' $null
if ($workflow.id -in $page.items.id) { throw 'A lista padrão inclui um workflow arquivado.' }
$stored = Invoke-Api GET $path $null
if ($stored.currentPublishedVersionId -ne $publishedId -or $stored.revision -ne $workflow.revision) {
    throw 'O detalhe persistido divergiu da operação.'
}
Write-Host "Roteiro da Fase 4 aprovado. Workflow arquivado: $($workflow.id). Nenhum node foi executado."
