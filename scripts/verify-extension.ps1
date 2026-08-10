[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$extensionRoot = Join-Path $repositoryRoot 'extensions\chromium'

Get-Content -Raw (Join-Path $extensionRoot 'manifest.json') | ConvertFrom-Json | Out-Null
foreach ($script in @('service-worker.js', 'content-script.js', 'options.js')) {
    & node --check (Join-Path $extensionRoot $script)
    if ($LASTEXITCODE -ne 0) {
        throw "Extension syntax validation failed for $script."
    }
}

Write-Host 'Chromium extension manifest and JavaScript syntax are valid.'
