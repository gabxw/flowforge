[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$targets = @(
    @{ Uri = 'http://127.0.0.1:5080/health/live'; ExpectedText = 'Healthy' },
    @{ Uri = 'http://127.0.0.1:5173/api/health/live'; ExpectedText = 'Healthy' },
    @{ Uri = 'http://127.0.0.1:5173/'; ExpectedText = 'id="root"' }
)

foreach ($target in $targets) {
    $response = Invoke-WebRequest -Uri $target.Uri -UseBasicParsing -TimeoutSec 10
    if ($response.StatusCode -ne 200 -or $response.Content -notmatch [regex]::Escape($target.ExpectedText)) {
        throw "Unexpected HTTP response from $($target.Uri)"
    }
    Write-Host "PASS $($target.Uri)"
}

Write-Host 'HTTP smoke passed. This checks phase 1 liveness and proxy, not database/broker integration.'
