[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$venv = Join-Path $root '.tools\datahub-venv'

# Keep the judge-facing OSS/Core stack deterministic. This pinned stable CLI
# release drives `datahub docker quickstart` and is new enough for the official
# MCP save_document tool, which requires DataHub OSS >= 1.4.0.
$dataHubCliVersion = '1.6.0.15'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker Desktop is required. Install and start Docker, then rerun this script.' }
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is installed but not running. Start Docker Desktop, then rerun this script.' }
if (-not (Get-Command py -ErrorAction SilentlyContinue) -and -not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Python 3.11+ is required for DataHub and the official DataHub MCP Server.' }

# Prefer the Windows launcher so an older `python` alias cannot mask an
# installed 3.11+ interpreter (common on clean judge machines).
$pythonCommand = 'python'
$pythonArguments = @()
if (Get-Command py -ErrorAction SilentlyContinue) {
    $pythonCommand = 'py'
    $pythonArguments = @('-3.12')
    & $pythonCommand @pythonArguments -c "import sys; raise SystemExit(0 if sys.version_info >= (3,11) else 1)" *> $null
    if ($LASTEXITCODE -ne 0) {
        $pythonArguments = @('-3.11')
        & $pythonCommand @pythonArguments -c "import sys; raise SystemExit(0 if sys.version_info >= (3,11) else 1)" *> $null
        if ($LASTEXITCODE -ne 0) { $pythonCommand = 'python'; $pythonArguments = @() }
    }
}

$pythonVersion = & $pythonCommand @pythonArguments -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
if ($LASTEXITCODE -ne 0) { throw 'Unable to determine the installed Python version.' }
$parts = $pythonVersion.Trim().Split('.')
if ([int]$parts[0] -lt 3 -or ([int]$parts[0] -eq 3 -and [int]$parts[1] -lt 11)) {
    throw "Python 3.11+ is required by the official DataHub MCP Server. Found Python $pythonVersion."
}

if (-not (Test-Path $venv)) { & $pythonCommand @pythonArguments -m venv $venv }
$venvPython = Join-Path $venv 'Scripts\python.exe'
$cli = Join-Path $venv 'Scripts\datahub.exe'
$uvx = Join-Path $venv 'Scripts\uvx.exe'
& $venvPython -m pip install --upgrade pip "acryl-datahub==$dataHubCliVersion" uv
if ($LASTEXITCODE -ne 0) { throw 'Unable to install the pinned DataHub CLI and MCP launcher dependencies.' }
if (-not (Test-Path $uvx)) { throw 'uvx was not installed; the DataHub MCP Server cannot be launched.' }

$installedDataHubVersion = & $venvPython -c "from importlib.metadata import version; print(version('acryl-datahub'))"
if ($LASTEXITCODE -ne 0 -or $installedDataHubVersion.Trim() -ne $dataHubCliVersion) {
    throw "Expected acryl-datahub $dataHubCliVersion but found '$($installedDataHubVersion.Trim())'. Refusing a non-reproducible judge stack."
}
Write-Host "Pinned DataHub OSS/Core CLI: acryl-datahub $dataHubCliVersion"

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
