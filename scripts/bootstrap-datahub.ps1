[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$venv = Join-Path $root '.tools\datahub-venv'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker Desktop is required. Install and start Docker, then rerun this script.' }
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is installed but not running. Start Docker Desktop, then rerun this script.' }
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Python 3.11+ is required for DataHub and the official DataHub MCP Server.' }

$pythonVersion = python -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
if ($LASTEXITCODE -ne 0) { throw 'Unable to determine the installed Python version.' }
$parts = $pythonVersion.Trim().Split('.')
if ([int]$parts[0] -lt 3 -or ([int]$parts[0] -eq 3 -and [int]$parts[1] -lt 11)) {
    throw "Python 3.11+ is required by the official DataHub MCP Server. Found Python $pythonVersion."
}

if (-not (Test-Path $venv)) { python -m venv $venv }
$venvPython = Join-Path $venv 'Scripts\python.exe'
$cli = Join-Path $venv 'Scripts\datahub.exe'
$uvx = Join-Path $venv 'Scripts\uvx.exe'
& $venvPython -m pip install --upgrade pip acryl-datahub uv
if ($LASTEXITCODE -ne 0) { throw 'Unable to install the DataHub CLI and MCP launcher dependencies.' }
if (-not (Test-Path $uvx)) { throw 'uvx was not installed; the DataHub MCP Server cannot be launched.' }

& $cli docker quickstart
if ($LASTEXITCODE -ne 0) { throw 'DataHub quickstart failed. Review Docker Desktop resources and try again.' }

for ($attempt = 1; $attempt -le 30; $attempt++) {
    try {
        Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 'http://localhost:9002' *> $null
        Write-Host 'DataHub is ready at http://localhost:9002'
        Write-Host "Official DataHub MCP launcher is ready at $uvx"
        exit 0
    } catch {
        Start-Sleep -Seconds 2
    }
}
throw 'DataHub did not become healthy on port 9002 within 60 seconds.'
