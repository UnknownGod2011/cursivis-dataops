[CmdletBinding()]
param(
    [switch]$ConfirmWriteback
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$mcpRequestTimeout = [TimeSpan]::FromSeconds(60)

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

# stderr may contain startup diagnostics. Drain it continuously so a verbose
# server cannot fill the redirected pipe and deadlock the judge-facing preflight.
# Do not print it because it may contain local catalog/configuration details.
$stderrDrain = $process.StandardError.ReadToEndAsync()

$nextId = 0
function Invoke-McpRequest {
    param([string]$Method, [hashtable]$Params)
    $script:nextId++
    $id = $script:nextId
    $payload = @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 20 -Compress
    $process.StandardInput.WriteLine($payload)
    $process.StandardInput.Flush()

    while ($true) {
        $readTask = $process.StandardOutput.ReadLineAsync()
        if (-not $readTask.Wait($mcpRequestTimeout)) {
            throw "DataHub MCP request '$Method' timed out after $([int]$mcpRequestTimeout.TotalSeconds) seconds."
        }
        $line = $readTask.Result
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

function Assert-McpToolArguments {
    param(
        [hashtable]$ToolsByName,
        [string]$Name,
        [string[]]$Arguments
    )

    $tool = $ToolsByName[$Name]
    if ($null -eq $tool) {
        throw "Official DataHub MCP Server is missing required tool '$Name'."
    }
    if ($null -eq $tool.inputSchema -or $null -eq $tool.inputSchema.properties) {
        throw "DataHub MCP tool '$Name' did not publish an MCP input schema."
    }

    $published = @($tool.inputSchema.properties.PSObject.Properties.Name)
    foreach ($argument in $Arguments) {
        if ($published -notcontains $argument) {
            throw "DataHub MCP tool '$Name' no longer exposes expected argument '$argument'. The Cursivis golden-flow contract must be reviewed before demoing."
        }
    }
}

function Assert-McpToolReadOnlyHint {
    param(
        [hashtable]$ToolsByName,
        [string]$Name,
        [bool]$Expected
    )

    $tool = $ToolsByName[$Name]
    if ($null -eq $tool -or $null -eq $tool.annotations) {
        throw "DataHub MCP tool '$Name' did not publish MCP safety annotations."
    }
    $property = $tool.annotations.PSObject.Properties['readOnlyHint']
    if ($null -eq $property -or [bool]$property.Value -ne $Expected) {
        throw "DataHub MCP tool '$Name' published an unexpected readOnlyHint. Refusing to trust a changed read/write safety contract."
    }
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
    $toolsByName = @{}
    foreach ($tool in @($toolList.tools)) {
        $toolsByName[[string]$tool.name] = $tool
    }

    $required = @('search', 'get_entities', 'list_schema_fields', 'get_lineage')
    foreach ($tool in $required) {
        if (-not $toolsByName.ContainsKey($tool)) {
            throw "Official DataHub MCP Server is missing required tool '$tool'."
        }
    }
    if (-not $toolsByName.ContainsKey('save_document')) {
        throw "Official DataHub MCP Server is missing required write-back tool 'save_document'."
    }

    # Validate the exact live MCP contracts used by the desktop golden flow.
    # This is intentionally non-mutating unless -ConfirmWriteback is supplied.
    Assert-McpToolArguments -ToolsByName $toolsByName -Name 'search' -Arguments @('query', 'num_results', 'offset')
    Assert-McpToolArguments -ToolsByName $toolsByName -Name 'get_entities' -Arguments @('urns')
    Assert-McpToolArguments -ToolsByName $toolsByName -Name 'list_schema_fields' -Arguments @('urn', 'limit', 'offset')
    Assert-McpToolArguments -ToolsByName $toolsByName -Name 'get_lineage' -Arguments @('urn', 'upstream', 'max_hops', 'max_results', 'offset')
    Assert-McpToolArguments -ToolsByName $toolsByName -Name 'save_document' -Arguments @('document_type', 'title', 'content', 'related_assets')
    foreach ($tool in $required) {
        Assert-McpToolReadOnlyHint -ToolsByName $toolsByName -Name $tool -Expected $true
    }
    Assert-McpToolReadOnlyHint -ToolsByName $toolsByName -Name 'save_document' -Expected $false

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
    if ($entityJson -notmatch [regex]::Escape('urn:li:corpuser:datahub')) {
        throw 'DataHub MCP entity read is missing the deterministic owner urn:li:corpuser:datahub.'
    }

    $schema = Invoke-McpTool -Name 'list_schema_fields' -Arguments @{ urn = $urn; limit = 100; offset = 0 }
    $schemaJson = $schema | ConvertTo-Json -Depth 50 -Compress
    foreach ($field in @('customer_id', 'lifetime_value_usd', 'customer_tier', 'updated_at')) {
        if ($schemaJson -notmatch [regex]::Escape($field)) {
            throw "DataHub MCP schema read is missing expected field '$field'."
        }
    }

    $upstream = Invoke-McpTool -Name 'get_lineage' -Arguments @{ urn = $urn; upstream = $true; max_hops = 3; max_results = 20; offset = 0 }
    $upstreamJson = $upstream | ConvertTo-Json -Depth 50 -Compress
    if ($upstreamJson -notmatch [regex]::Escape('raw.customers')) {
        throw "DataHub MCP upstream lineage is missing expected source 'raw.customers'."
    }

    $downstream = Invoke-McpTool -Name 'get_lineage' -Arguments @{ urn = $urn; upstream = $false; max_hops = 3; max_results = 20; offset = 0 }
    $downstreamJson = $downstream | ConvertTo-Json -Depth 50 -Compress
    foreach ($asset in @('executive_revenue', 'churn_prediction_features')) {
        if ($downstreamJson -notmatch [regex]::Escape($asset)) {
            throw "DataHub MCP downstream lineage is missing expected asset '$asset'."
        }
    }

    Write-Host 'Live DataHub MCP preflight passed.' -ForegroundColor Green
    Write-Host "Resolved dataset: $urn"
    Write-Host 'Verified MCP context: deterministic owner, schema, raw.customers upstream, and both downstream blast-radius assets.'
    Write-Host 'Verified live MCP schemas and read/write safety annotations for search, get_entities, list_schema_fields, get_lineage, and save_document.'

    if ($ConfirmWriteback) {
        # Supplying -ConfirmWriteback is an explicit human confirmation. The default
        # preflight remains read-only so setup and CI can never mutate DataHub.
        $marker = 'CURSIVIS-MCP-WRITEBACK-' + [Guid]::NewGuid().ToString('N')
        $title = "Cursivis MCP acceptance proof - $marker"
        $content = "Confirmed Cursivis DataOps MCP write-back proof. Marker: $marker"
        Write-Host 'Explicit -ConfirmWriteback received; performing one durable DataHub MCP save_document mutation.' -ForegroundColor Yellow

        $saved = Invoke-McpTool -Name 'save_document' -Arguments @{
            document_type = 'Decision'
            title = $title
            content = $content
            related_assets = @($urn)
        }
        $savedJson = $saved | ConvertTo-Json -Depth 50 -Compress
        $documentMatch = [regex]::Match($savedJson, 'urn:li:document:[A-Za-z0-9._:-]+')
        if (-not $documentMatch.Success) {
            throw 'DataHub MCP save_document returned no document URN; durable write-back cannot be proven.'
        }
        $documentUrn = $documentMatch.Value

        $verifiedDocument = Invoke-McpTool -Name 'get_entities' -Arguments @{ urns = $documentUrn }
        $verifiedJson = $verifiedDocument | ConvertTo-Json -Depth 50 -Compress
        foreach ($expected in @($documentUrn, $title, $marker, $urn)) {
            if ($verifiedJson -notmatch [regex]::Escape($expected)) {
                throw "DataHub MCP read-after-write did not return expected persisted value '$expected'."
            }
        }

        Write-Host 'Confirmed DataHub MCP write-back passed read-after-write verification.' -ForegroundColor Green
        Write-Host "Saved document: $documentUrn"
        Write-Host 'Verified persisted title/content marker and related analytics.customers asset through MCP get_entities.'
    } else {
        Write-Host 'save_document is exposed for the confirmation-gated app flow; preflight itself performed no mutation.'
        Write-Host 'Optional live write proof: rerun with -ConfirmWriteback only when you intentionally want to create a verification document.'
    }
}
finally {
    try { $process.StandardInput.Close() } catch {}
    if (-not $process.HasExited) {
        try { $process.Kill($true) } catch {}
    }
    try { $null = $stderrDrain.Wait([TimeSpan]::FromSeconds(5)) } catch {}
    $process.Dispose()
}
