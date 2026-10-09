[CmdletBinding()]
param(
    [string] $ApiBaseUri = 'http://127.0.0.1:5080',
    [string] $FrontendBaseUri = 'http://127.0.0.1:5173'
)

$ErrorActionPreference = 'Stop'
$targets = @(
    @{ Uri = "$ApiBaseUri/health/live"; ExpectedText = 'Healthy' },
    @{ Uri = "$FrontendBaseUri/api/health/live"; ExpectedText = 'Healthy' },
    @{ Uri = "$FrontendBaseUri/"; ExpectedText = 'id="root"' }
)

foreach ($target in $targets) {
    $response = Invoke-WebRequest -Uri $target.Uri -UseBasicParsing -TimeoutSec 10
    if ($response.StatusCode -ne 200 -or $response.Content -notmatch [regex]::Escape($target.ExpectedText)) {
        throw "Unexpected HTTP response from $($target.Uri)"
    }
    Write-Host "PASS $($target.Uri)"
}

Write-Host 'Liveness, shell e proxy aprovados. Use smoke-workflows.ps1 para verificar a API e o PostgreSQL.'
