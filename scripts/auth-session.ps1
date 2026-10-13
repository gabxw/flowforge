# Sessões ficam somente em memória da execução PowerShell. Não imprimir tokens ou passwords.
function Get-FlowForgeSmokeHeaders([string] $BaseUri) {
    if (-not $global:FlowForgeSmokeSessions) { $global:FlowForgeSmokeSessions = @{} }
    if ($global:FlowForgeSmokeSessions.ContainsKey($BaseUri)) {
        $cached = $global:FlowForgeSmokeSessions[$BaseUri]
        if ($cached.Expires -gt [DateTimeOffset]::UtcNow.AddSeconds(30)) { return @{ Authorization = 'Bearer ' + $cached.Token } }
    }
    $email = $env:FLOWFORGE_SMOKE_EMAIL; $password = $env:FLOWFORGE_SMOKE_PASSWORD
    if (-not $email -or -not $password) { throw 'Configure FLOWFORGE_SMOKE_EMAIL e FLOWFORGE_SMOKE_PASSWORD na sessão antes dos smokes autenticados.' }
    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $csrf = Invoke-RestMethod "$BaseUri/api/auth/csrf" -WebSession $session -TimeoutSec 10
    $json = [Text.Encoding]::UTF8.GetBytes((@{email=$email;password=$password} | ConvertTo-Json -Compress))
    $arguments = @{Method='POST'; Uri="$BaseUri/api/auth/login"; WebSession=$session; Headers=@{'X-CSRF-Token'=$csrf.csrfToken}; ContentType='application/json'; Body=$json; SkipHttpErrorCheck=$true; TimeoutSec=20}
    $response = Invoke-WebRequest @arguments
    if ($response.StatusCode -eq 401) {
        $arguments.Uri = "$BaseUri/api/auth/register"; $response = Invoke-WebRequest @arguments
    }
    if ($response.StatusCode -notin 200,201) { throw "A autenticação do smoke falhou (HTTP $($response.StatusCode))." }
    $result = $response.Content | ConvertFrom-Json
    $global:FlowForgeSmokeSessions[$BaseUri] = @{Token=$result.accessToken;Expires=[DateTimeOffset] $result.expiresAt}
    return @{Authorization='Bearer '+$result.accessToken}
}
