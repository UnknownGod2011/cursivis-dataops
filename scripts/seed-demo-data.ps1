[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$python = Join-Path $root '.tools\datahub-venv\Scripts\python.exe'
$seed = Join-Path $PSScriptRoot 'seed_demo_data.py'

if (-not (Test-Path $python)) { throw 'Run .\scripts\bootstrap-datahub.ps1 first.' }
if (-not (Test-Path $seed)) { throw 'Missing scripts\seed_demo_data.py.' }

if ([string]::IsNullOrWhiteSpace($env:DATAHUB_GMS_URL)) {
    $env:DATAHUB_GMS_URL = 'http://localhost:8080'
}
if ([string]::IsNullOrWhiteSpace($env:DATAHUB_GRAPHQL_URL)) {
    $env:DATAHUB_GRAPHQL_URL = "$($env:DATAHUB_GMS_URL.TrimEnd('/'))/api/graphql"
}

Write-Host 'Seeding deterministic Cursivis DataOps demo metadata into DataHub...'
& $python $seed
if ($LASTEXITCODE -ne 0) {
    throw 'Deterministic DataHub demo metadata seed or verification failed.'
}

Write-Host 'Cursivis DataOps demo catalog is ready and verified.' -ForegroundColor Green
Write-Host 'Canonical asset: postgres / analytics.customers / PROD'
Write-Host 'Use examples\broken-query.sql for the golden demo.'
