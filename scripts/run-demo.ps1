[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$envFile = Join-Path $root '.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$' -and -not $_.TrimStart().StartsWith('#')) { Set-Item -Path ("Env:" + $matches[1]) -Value $matches[2] }
    }
}
if ([string]::IsNullOrWhiteSpace($env:GEMINI_API_KEY)) { throw 'Set GEMINI_API_KEY in your process or .env before running the demo.' }
if ([string]::IsNullOrWhiteSpace($env:DATAHUB_GRAPHQL_URL)) { $env:DATAHUB_GRAPHQL_URL = 'http://localhost:9002/api/graphql' }
try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 'http://localhost:9002' *> $null } catch { throw 'DataHub is unavailable. Run .\scripts\bootstrap-datahub.ps1 first.' }
dotnet build (Join-Path $root 'Cursivis.sln') -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
$app = Join-Path $root 'apps\windows\Cursivis.Windows.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\Cursivis.exe'
if (-not (Test-Path $app)) { throw "Expected application executable was not produced: $app" }
Start-Process -FilePath $app
Write-Host 'Cursivis DataOps launched. Select examples\broken-query.sql and invoke the configured context hotkey.'
