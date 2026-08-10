[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0-beta.1'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $root 'artifacts'
$publishRoot = Join-Path $artifacts 'publish'
$releaseRoot = Join-Path $artifacts 'release'
$innoCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$innoCompiler = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($innoCompiler)) {
    throw 'Inno Setup 6 is required. Install JRSoftware.InnoSetup with winget.'
}

foreach ($path in @($publishRoot, $releaseRoot)) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    if (-not $fullPath.StartsWith($artifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the repository artifacts directory: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath | Out-Null
}

$appProject = Join-Path $root 'apps\windows\Cursivis.Windows.App\Cursivis.Windows.App.csproj'
$nativeHostProject = Join-Path $root 'apps\windows\Cursivis.Windows.NativeHost\Cursivis.Windows.NativeHost.csproj'

dotnet publish $appProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:WindowsAppSDKSelfContained=true `
    -p:VersionPrefix=0.1.0 `
    -o (Join-Path $publishRoot 'app')
if ($LASTEXITCODE -ne 0) { throw 'Cursivis app publish failed.' }

# WinUI's unpackaged publish target omits application-owned PRI/XBF files when
# a custom PublishDir is used. Copy those compiled resources from TargetDir;
# without them the self-contained executable fails with XamlParseException.
$appBuildOutput = Join-Path $root 'apps\windows\Cursivis.Windows.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64'
$resourceFiles = @(
    Get-ChildItem -LiteralPath $appBuildOutput -Recurse -File |
        Where-Object { $_.Extension -eq '.xbf' -or $_.Name -eq 'Cursivis.pri' }
)
if ($resourceFiles.Count -eq 0) {
    throw 'The WinUI publish produced no application resource files.'
}
foreach ($resource in $resourceFiles) {
    $relativePath = $resource.FullName.Substring($appBuildOutput.Length).TrimStart('\')
    $destination = Join-Path (Join-Path $publishRoot 'app') $relativePath
    $destinationDirectory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $resource.FullName -Destination $destination -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'app\Cursivis.pri'))) {
    throw 'The WinUI publish is missing Cursivis.pri.'
}

dotnet publish $nativeHostProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:VersionPrefix=0.1.0 `
    -o (Join-Path $publishRoot 'native-host')
if ($LASTEXITCODE -ne 0) { throw 'Cursivis native host publish failed.' }

& $innoCompiler "/DMyAppVersion=$Version" (Join-Path $root 'installer\Cursivis.iss')
if ($LASTEXITCODE -ne 0) { throw 'Cursivis installer compilation failed.' }

$installer = Join-Path $releaseRoot 'Cursivis-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $installer)) {
    throw 'The installer did not produce the expected release asset.'
}

$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$versionedInstaller = Join-Path $releaseRoot "Cursivis-Setup-$Version-x64.exe"
Copy-Item -LiteralPath $installer -Destination $versionedInstaller -Force
$checksumFile = "$versionedInstaller.sha256"
Set-Content -LiteralPath $checksumFile -Value "$hash  $(Split-Path -Leaf $versionedInstaller)" -Encoding ascii
[pscustomobject]@{
    Version = $Version
    Installer = $installer
    VersionedInstaller = $versionedInstaller
    ChecksumFile = $checksumFile
    Sha256 = $hash
    Bytes = (Get-Item -LiteralPath $installer).Length
}
