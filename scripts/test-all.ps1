[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    .\scripts\check-secrets.ps1
    .\scripts\verify-extension.ps1
    dotnet restore Cursivis.sln --locked-mode -r win-x64 -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build Cursivis.sln -c Release -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test Cursivis.sln -c Release -p:Platform=x64 --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally { Pop-Location }
