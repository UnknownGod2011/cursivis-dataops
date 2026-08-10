[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repositoryRoot
try {
    git rev-parse --is-inside-work-tree | Out-Null

    git check-ignore --quiet -- '.env'
    if ($LASTEXITCODE -ne 0) {
        throw 'The root .env file is not ignored.'
    }

    $patterns = @(
        'sk-[A-Za-z0-9_-]{20,}',
        '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
        '(api[_-]?key|authorization)[[:space:]]*[:=][[:space:]]*[A-Za-z0-9_./+=-]{20,}'
    )

    $flagged = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($pattern in $patterns) {
        $files = & git grep --cached --files-with-matches --ignore-case --extended-regexp -- $pattern 2>$null
        if ($LASTEXITCODE -notin @(0, 1)) {
            throw 'The git secret scan command failed.'
        }
        foreach ($file in $files) {
            [void]$flagged.Add($file)
        }
    }

    if ($flagged.Count -gt 0) {
        Write-Error ("Potential secret material was found in tracked files: {0}" -f (($flagged | Sort-Object) -join ', '))
        exit 1
    }

    # A successful no-match from git grep is exit code 1. PowerShell otherwise
    # propagates that stale native exit code after this script completes.
    $global:LASTEXITCODE = 0
    Write-Host 'Secret scan passed; .env is ignored and no tracked credential pattern was found.'
}
finally {
    Pop-Location
}
