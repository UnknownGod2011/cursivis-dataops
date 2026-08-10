[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$venv = Join-Path $root '.tools\datahub-venv'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker Desktop is required. Install and start Docker, then rerun this script.' }
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is installed but not running. Start Docker Desktop, then rerun this script.' }
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Python 3.10+ is required to install the DataHub CLI.' }

if (-not (Test-Path $venv)) { python -m venv $venv }
$cli = Join-Path $venv 'Scripts\datahub.exe'
& (Join-Path $venv 'Scripts\python.exe') -m pip install --upgrade pip acryl-datahub
if ($LASTEXITCODE -ne 0) { throw 'Unable to install the DataHub CLI.' }
& $cli docker quickstart
if ($LASTEXITCODE -ne 0) { throw 'DataHub quickstart failed. Review Docker Desktop resources and try again.' }

for ($attempt = 1; $attempt -le 30; $attempt++) {
    try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 'http://localhost:9002' *> $null; Write-Host 'DataHub is ready at http://localhost:9002'; exit 0 } catch { Start-Sleep -Seconds 2 }
}
throw 'DataHub did not become healthy on port 9002 within 60 seconds.'
