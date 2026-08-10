[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$envFile = Join-Path $root '.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$' -and -not $_.TrimStart().StartsWith('#')) {
            Set-Item -Path ("Env:" + $matches[1]) -Value $matches[2]
        }
    }
}

if ([string]::IsNullOrWhiteSpace($env:GEMINI_API_KEY)) {
    throw 'Set GEMINI_API_KEY in your process or .env before running the demo.'
}
if ([string]::IsNullOrWhiteSpace($env:DATAHUB_GMS_URL)) {
    $env:DATAHUB_GMS_URL = 'http://localhost:8080'
}
if ([string]::IsNullOrWhiteSpace($env:DATAHUB_GRAPHQL_URL)) {
    # Retained for deterministic seed/verification helpers. The judge-facing
    # Cursivis grounding path reads DataHub through MCP, not direct GraphQL.
    $env:DATAHUB_GRAPHQL_URL = "$($env:DATAHUB_GMS_URL.TrimEnd('/'))/api/graphql"
}

if ([string]::IsNullOrWhiteSpace($env:DATAHUB_MCP_COMMAND)) {
    $uvx = Join-Path $root '.tools\datahub-venv\Scripts\uvx.exe'
    if (-not (Test-Path $uvx)) {
        throw 'Official DataHub MCP launcher is missing. Run .\scripts\bootstrap-datahub.ps1 first.'
    }
    $env:DATAHUB_MCP_COMMAND = $uvx
}
if ([string]::IsNullOrWhiteSpace($env:DATAHUB_MCP_PACKAGE)) {
    # Pin the official MCP server used by the submission. This release exposes
    # search/get_entities/list_schema_fields/get_lineage and save_document, and
    # requires Python 3.11+. Avoid @latest so judging is reproducible.
    $env:DATAHUB_MCP_PACKAGE = 'mcp-server-datahub@0.6.0'
}

try {
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 $env:DATAHUB_GMS_URL *> $null
} catch {
    throw 'DataHub GMS is unavailable. Run .\scripts\bootstrap-datahub.ps1 first.'
}

# The demo must use the deterministic catalog, not generic quickstart metadata.
& (Join-Path $PSScriptRoot 'seed-demo-data.ps1')
if ($LASTEXITCODE -ne 0) { throw 'DataHub demo metadata verification failed.' }

dotnet build (Join-Path $root 'Cursivis.sln') -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

$app = Join-Path $root 'apps\windows\Cursivis.Windows.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\Cursivis.exe'
if (-not (Test-Path $app)) { throw "Expected application executable was not produced: $app" }

Start-Process -FilePath $app
Write-Host 'Cursivis DataOps launched with a verified local DataHub catalog and official MCP runtime.' -ForegroundColor Green
Write-Host "Pinned MCP package: $($env:DATAHUB_MCP_PACKAGE)"
Write-Host 'Golden demo: select examples\broken-query.sql and invoke the configured context hotkey.'
Write-Host 'Grounding uses DataHub MCP search/get_entities/list_schema_fields/get_lineage; confirmed Save to DataHub uses MCP save_document + read-after-write.'
