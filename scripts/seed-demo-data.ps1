[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cli = Join-Path $root '.tools\datahub-venv\Scripts\datahub.exe'
if (-not (Test-Path $cli)) { throw 'Run .\scripts\bootstrap-datahub.ps1 first.' }
& $cli docker ingest-sample-data
if ($LASTEXITCODE -ne 0) { throw 'DataHub sample metadata ingestion failed.' }
Write-Host 'Sample metadata is available. For the scripted Cursivis story, use the assets and expected evidence in examples/.'
