[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ([string]::IsNullOrWhiteSpace($env:DATAHUB_GMS_URL)) {
    $env:DATAHUB_GMS_URL = 'http://localhost:8080'
}
if ([string]::IsNullOrWhiteSpace($env:DATAHUB_MCP_COMMAND)) {
    $candidate = Join-Path $root '.tools\datahub-venv\Scripts\uvx.exe'
    if (-not (Test-Path $candidate)) {
        throw 'Official DataHub MCP launcher is missing. Run .\scripts\bootstrap-datahub.ps1 first.'
    }
    $env:DATAHUB_MCP_COMMAND = $candidate
}
if ([string]::IsNullOrWhiteSpace($env:DATAHUB_MCP_PACKAGE)) {
    $env:DATAHUB_MCP_PACKAGE = 'mcp-server-datahub@0.6.0'
}

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $env:DATAHUB_MCP_COMMAND
$null = $psi.ArgumentList.Add($env:DATAHUB_MCP_PACKAGE)
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.Environment['DATAHUB_GMS_URL'] = $env:DATAHUB_GMS_URL.TrimEnd('/')
if (-not [string]::IsNullOrWhiteSpace($env:DATAHUB_TOKEN)) {
    $psi.Environment['DATAHUB_GMS_TOKEN'] = $env:DATAHUB_TOKEN.Trim()
}
# save_document is a document tool with its own feature flag in the official
# DataHub MCP server. Keep unrelated metadata mutation tools disabled while
# enabling exactly the document write capability exercised by the confirmed
# Cursivis write-back flow.
$psi.Environment['TOOLS_IS_MUTATION_ENABLED'] = 'false'
$psi.Environment['DATAHUB_MCP_DOCUMENT_TOOLS_DISABLED'] = 'false'
$psi.Environment['SAVE_DOCUMENT_TOOL_ENABLED'] = 'true'
$psi.Environment['PYTHONUNBUFFERED'] = '1'

$process = [System.Diagnostics.Process]::Start($psi)
if ($null -eq $process) { throw 'Could not start the official DataHub MCP Server.' }

$nextId = 0
function Invoke-McpRequest {
    param([string]$Method, [hashtable]$Params)
    $script:nextId++
    $id = $script:nextId
    $payload = @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 20 -Compress
    $process.StandardInput.WriteLine($payload)
    $process.StandardInput.Flush()

    while ($true) {
        $line = $process.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "DataHub MCP Server closed stdout while waiting for '$Method'." }
        try { $message = $line | ConvertFrom-Json -Depth 50 } catch { continue }
        if ($null -eq $message.id -or [long]$message.id -ne $id) { continue }
        if ($null -ne $message.error) { throw "DataHub MCP request '$Method' failed." }
        if ($null -eq $message.result) { throw "DataHub MCP request '$Method' returned no result." }
        return $message.result
    }
}

function Invoke-McpTool {
    param([string]$Name, [hashtable]$Arguments)
    $result = Invoke-McpRequest -Method 'tools/call' -Params @{ name = $Name; arguments = $Arguments }
    if ($result.isError -eq $true) { throw "DataHub MCP tool '$Name' reported an error." }
    return $result
}

try {
    $initialize = Invoke-McpRequest -Method 'initialize' -Params @{
        protocolVersion = '2025-06-18'
        capabilities = @{}
        clientInfo = @{ name = 'cursivis-dataops-preflight'; version = '1.0' }
    }
    if ([string]::IsNullOrWhiteSpace([string]$initialize.protocolVersion)) {
        throw 'DataHub MCP did not complete protocol initialization.'
    }

    $notification = @{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = @{} } | ConvertTo-Json -Depth 10 -Compress
    $process.StandardInput.WriteLine($notification)
    $process.StandardInput.Flush()

    $toolList = Invoke-McpRequest -Method 'tools/list' -Params @{}
    $available = @($toolList.tools | ForEach-Object { [string]$_.name })
    $required = @('search', 'get_entities', 'list_schema_fields', 'get_lineage')
    foreach ($tool in $required) {
        if ($available -notcontains $tool) {
            throw "Official DataHub MCP Server is missing required tool '$tool'."
        }
    }
    if ($available -notcontains 'save_document') {
        throw "Official DataHub MCP Server is missing required write-back tool 'save_document'."
    }

    $search = Invoke-McpTool -Name 'search' -Arguments @{ query = '/q analytics+customers'; num_results = 5; offset = 0 }
    $searchJson = $search | ConvertTo-Json -Depth 50 -Compress
    $urnMatch = [regex]::Match($searchJson, 'urn:li:dataset:[^"\\]+')
    if (-not $urnMatch.Success) {
        throw 'DataHub MCP search could not resolve the deterministic analytics.customers dataset.'
    }
    $urn = $urnMatch.Value

    $entity = Invoke-McpTool -Name 'get_entities' -Arguments @{ urns = $urn }
    $entityJson = $entity | ConvertTo-Json -Depth 50 -Compress
    if ($entityJson -notmatch '(?i)analytics\.customers|customers') {
        throw 'DataHub MCP entity read did not confidently match analytics.customers.'
    }

    $schema = Invoke-McpTool -Name 'list_schema_fields' -Arguments @{ urn = $urn; limit = 100; offset = 0 }
    $schemaJson = $schema | ConvertTo-Json -Depth 50 -Compress
    foreach ($field in @('customer_id', 'lifetime_value_usd', 'customer_tier', 'updated_at')) {
        if ($schemaJson -notmatch [regex]::Escape($field)) {
            throw "DataHub MCP schema read is missing expected field '$field'."
        }
    }

    $null = Invoke-McpTool -Name 'get_lineage' -Arguments @{ urn = $urn; upstream = $true; max_hops = 3; max_results = 20; offset = 0 }
    $downstream = Invoke-McpTool -Name 'get_lineage' -Arguments @{ urn = $urn; upstream = $false; max_hops = 3; max_results = 20; offset = 0 }
    $downstreamJson = $downstream | ConvertTo-Json -Depth 50 -Compress
    foreach ($asset in @('executive_revenue', 'churn_prediction_features')) {
        if ($downstreamJson -notmatch [regex]::Escape($asset)) {
            throw "DataHub MCP downstream lineage is missing expected asset '$asset'."
        }
    }

    Write-Host 'Live DataHub MCP preflight passed.' -ForegroundColor Green
    Write-Host "Resolved dataset: $urn"
    Write-Host 'Verified runtime tools: search, get_entities, list_schema_fields, get_lineage; save_document is exposed for the confirmation-gated app flow.'
}
finally {
    try { $process.StandardInput.Close() } catch {}
    if (-not $process.HasExited) {
        try { $process.Kill($true) } catch {}
    }
    $process.Dispose()
}
