using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;

namespace Cursivis.Infrastructure.OpenAI;

/// <summary>
/// Gemini reasoning provider whose data-aware path is grounded through the
/// official DataHub MCP Server. Deterministic DataHub SDK/GraphQL helpers remain
/// useful for local catalog setup, but judge-facing runtime reads and reviewed
/// knowledge write-back happen through MCP tools.
/// </summary>
public sealed partial class DataHubGeminiResponsesGateway : IResponsesGateway
{
    private const int MaximumGroundingCharacters = 30_000;
    private const int MaximumResolutionCharacters = 24_000;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _groundingGate = new();
    private string? _lastGroundedDatasetUrn;
    private string? _lastGroundedDatasetName;

    public bool HasGroundedDataset
    {
        get
        {
            lock (_groundingGate)
            {
                return !string.IsNullOrWhiteSpace(_lastGroundedDatasetUrn);
            }
        }
    }

    public async Task<StructuredResponseResult> CreateStructuredResponseAsync(
        StructuredResponseRequest request,
        CancellationToken cancellationToken = default)
    {
        // A displayed result must never remain write-eligible while another
        // reasoning request is replacing its DataHub grounding. The new target
        // is committed only after both MCP grounding and Gemini generation succeed.
        ClearGroundingTarget();

        try
        {
            GeminiConfiguration configuration = GeminiConfiguration.FromEnvironment(request.Model);
            GroundingResult grounding = await GetGroundingAsync(request.UserContent, configuration, cancellationToken)
                .ConfigureAwait(false);
            StructuredResponseResult result = await GenerateAsync(
                request,
                configuration,
                grounding.Context,
                cancellationToken).ConfigureAwait(false);
            ApplyGroundingOutcome(result.Succeeded, grounding.DatasetUrn, grounding.DatasetName);
            return result;
        }
        catch (GeminiGatewayException exception)
        {
            ClearGroundingTarget();
            return StructuredResponseResult.Failed(exception.Failure);
        }
        catch (DataHubMcpException exception)
        {
            ClearGroundingTarget();
            return StructuredResponseResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Network,
                $"DataHub MCP grounding failed safely: {exception.Message}",
                false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ClearGroundingTarget();
            return StructuredResponseResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Timeout,
                "The Gemini or DataHub MCP request timed out.",
                true));
        }
        catch (HttpRequestException)
        {
            ClearGroundingTarget();
            return StructuredResponseResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Network,
                "Cursivis could not reach Gemini.",
                true));
        }
        catch (JsonException)
        {
            ClearGroundingTarget();
            return StructuredResponseResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.MalformedResponse,
                "Gemini or DataHub MCP returned malformed structured data.",
                false));
        }
    }

    public async Task<ModelAvailabilityResult> CheckModelAvailabilityAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        try
        {
            GeminiConfiguration configuration = GeminiConfiguration.FromEnvironment(model);
            var request = new StructuredResponseRequest(
                configuration.Model,
                "Return JSON only.",
                "{\"ok\":true}",
                "readiness",
                "{\"type\":\"object\"}",
                TimeSpan.FromSeconds(20));
            StructuredResponseResult result = await GenerateAsync(
                request,
                configuration,
                string.Empty,
                cancellationToken).ConfigureAwait(false);
            return new ModelAvailabilityResult(configuration.Model, result.Succeeded, result.Failure, DateTimeOffset.UtcNow);
        }
        catch (GeminiGatewayException exception)
        {
            return new ModelAvailabilityResult(model, false, exception.Failure, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Saves a reviewed result only after the UI's explicit two-step confirmation.
    /// The official DataHub MCP save_document mutation performs the write and an
    /// MCP get_entities read verifies the new document before success is shown.
    /// </summary>
    public async Task<DataHubResolutionSaveResult> SaveResolutionAsync(
        string resolutionText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resolutionText))
        {
            return DataHubResolutionSaveResult.Failed("There is no reviewed resolution to save.");
        }

        string reviewed = resolutionText.Trim();
        if (reviewed.Length > MaximumResolutionCharacters)
        {
            return DataHubResolutionSaveResult.Failed(
                $"The reviewed resolution is too large to save exactly through DataHub MCP ({reviewed.Length:N0} characters; maximum {MaximumResolutionCharacters:N0}). Cursivis will not truncate confirmed content.");
        }

        string? datasetUrn;
        string? datasetName;
        lock (_groundingGate)
        {
            datasetUrn = _lastGroundedDatasetUrn;
            datasetName = _lastGroundedDatasetName;
        }
        if (string.IsNullOrWhiteSpace(datasetUrn))
        {
            return DataHubResolutionSaveResult.Failed(
                "No DataHub-grounded dataset is associated with this result. Run a grounded data request first.");
        }

        GeminiConfiguration configuration;
        try
        {
            configuration = GeminiConfiguration.FromEnvironment(
                Environment.GetEnvironmentVariable("GEMINI_MODEL")?.Trim() ?? "gemini-2.5-flash");
        }
        catch (GeminiGatewayException exception)
        {
            return DataHubResolutionSaveResult.Failed(exception.Failure.SafeMessage);
        }

        if (string.IsNullOrWhiteSpace(configuration.DataHubGmsUrl))
        {
            return DataHubResolutionSaveResult.Failed(
                "DataHub MCP is unavailable — configure DATAHUB_GMS_URL before saving a resolution.");
        }

        string title = $"Cursivis resolution — {datasetName ?? "DataHub dataset"}";

        try
        {
            await using DataHubMcpClient mcp = await DataHubMcpClient.StartAsync(
                configuration.DataHubGmsUrl,
                configuration.DataHubToken,
                enableMutations: true,
                cancellationToken).ConfigureAwait(false);
            RequireTools(mcp, "save_document", "get_entities");

            JsonElement saveResult = await mcp.CallToolAsync(
                "save_document",
                new
                {
                    document_type = "Decision",
                    title,
                    content = reviewed,
                    related_assets = new[] { datasetUrn },
                },
                cancellationToken).ConfigureAwait(false);
            string saveText = DataHubMcpClient.GetToolResultText(saveResult);
            string? documentUrn = FindFirstDocumentUrn(saveText);
            if (string.IsNullOrWhiteSpace(documentUrn))
            {
                return DataHubResolutionSaveResult.Failed(
                    "DataHub MCP accepted the save request but returned no document URN, so Cursivis cannot claim success.");
            }

            JsonElement verifyResult = await mcp.CallToolAsync(
                "get_entities",
                new { urns = documentUrn },
                cancellationToken).ConfigureAwait(false);
            string verified = DataHubMcpClient.GetToolResultText(verifyResult);
            if (!ContainsPersistedValue(verified, documentUrn) ||
                !ContainsPersistedValue(verified, title) ||
                !ContainsPersistedValue(verified, reviewed) ||
                !ContainsPersistedValue(verified, datasetUrn))
            {
                return DataHubResolutionSaveResult.Failed(
                    "The resolution was submitted through DataHub MCP, but read-after-write verification did not match the saved document content and related asset.");
            }

            return DataHubResolutionSaveResult.Success(documentUrn);
        }
        catch (DataHubMcpException exception)
        {
            return DataHubResolutionSaveResult.Failed($"DataHub MCP write-back failed safely: {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DataHubResolutionSaveResult.Failed("DataHub MCP timed out while saving the reviewed resolution.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return DataHubResolutionSaveResult.Failed("Cursivis could not start or communicate with the DataHub MCP Server.");
        }
    }

    private async Task<GroundingResult> GetGroundingAsync(
        string selectedText,
        GeminiConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string? dataset = ExtractDatasetReferences(selectedText).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(dataset))
        {
            return GroundingResult.Empty;
        }
        if (string.IsNullOrWhiteSpace(configuration.DataHubGmsUrl))
        {
            throw new GeminiGatewayException(new OpenAiFailure(
                OpenAiFailureKind.Network,
                "DataHub MCP context unavailable — configure DATAHUB_GMS_URL for grounded data work.",
                false));
        }

        await using DataHubMcpClient mcp = await DataHubMcpClient.StartAsync(
            configuration.DataHubGmsUrl,
            configuration.DataHubToken,
            enableMutations: false,
            cancellationToken).ConfigureAwait(false);
        RequireTools(mcp, "search", "get_entities", "list_schema_fields", "get_lineage");

        string searchQuery = "/q " + dataset.Replace(".", "+", StringComparison.Ordinal);
        JsonElement searchResult = await mcp.CallToolAsync(
            "search",
            new { query = searchQuery, num_results = 5, offset = 0 },
            cancellationToken).ConfigureAwait(false);
        string searchText = DataHubMcpClient.GetToolResultText(searchResult);
        string? urn = FindFirstDatasetUrn(searchText);
        if (string.IsNullOrWhiteSpace(urn))
        {
            string shortName = dataset.Split('.').Last();
            searchResult = await mcp.CallToolAsync(
                "search",
                new { query = "/q " + shortName, num_results = 10, offset = 0 },
                cancellationToken).ConfigureAwait(false);
            searchText = DataHubMcpClient.GetToolResultText(searchResult);
            urn = FindFirstDatasetUrn(searchText);
        }
        if (string.IsNullOrWhiteSpace(urn))
        {
            throw new GeminiGatewayException(new OpenAiFailure(
                OpenAiFailureKind.ModelUnavailable,
                "DataHub MCP found no dataset matching the selected SQL; Cursivis will not fabricate grounding.",
                false));
        }

        JsonElement entityResult = await mcp.CallToolAsync(
            "get_entities",
            new { urns = urn },
            cancellationToken).ConfigureAwait(false);
        JsonElement schemaResult = await mcp.CallToolAsync(
            "list_schema_fields",
            new { urn, limit = 100, offset = 0 },
            cancellationToken).ConfigureAwait(false);
        JsonElement upstreamResult = await mcp.CallToolAsync(
            "get_lineage",
            new { urn, upstream = true, max_hops = 3, max_results = 20, offset = 0 },
            cancellationToken).ConfigureAwait(false);
        JsonElement downstreamResult = await mcp.CallToolAsync(
            "get_lineage",
            new { urn, upstream = false, max_hops = 3, max_results = 20, offset = 0 },
            cancellationToken).ConfigureAwait(false);

        string entityText = DataHubMcpClient.GetToolResultText(entityResult);
        string schemaText = DataHubMcpClient.GetToolResultText(schemaResult);
        string upstreamText = DataHubMcpClient.GetToolResultText(upstreamResult);
        string downstreamText = DataHubMcpClient.GetToolResultText(downstreamResult);

        // Guard against a coincidental broad-search match. The canonical name or
        // its terminal segment must be represented in the retrieved entity.
        string terminalName = dataset.Split('.').Last();
        if (!entityText.Contains(dataset, StringComparison.OrdinalIgnoreCase) &&
            !entityText.Contains(terminalName, StringComparison.OrdinalIgnoreCase))
        {
            throw new GeminiGatewayException(new OpenAiFailure(
                OpenAiFailureKind.ModelUnavailable,
                "DataHub MCP search did not resolve the selected dataset confidently; Cursivis will not fabricate grounding.",
                false));
        }

        string combined = JsonSerializer.Serialize(new
        {
            source = "DataHub MCP Server",
            resolvedDataset = dataset,
            resolvedUrn = urn,
            search = Limit(searchText, 4_000),
            entity = Limit(entityText, 8_000),
            schema = Limit(schemaText, 9_000),
            upstreamLineage = Limit(upstreamText, 4_000),
            downstreamLineage = Limit(downstreamText, 4_000),
        });
        return new GroundingResult(
            Limit(combined, MaximumGroundingCharacters),
            urn,
            dataset);
    }

    private static void RequireTools(DataHubMcpClient mcp, params string[] names)
    {
        foreach (string name in names)
        {
            if (!mcp.HasTool(name))
            {
                throw new DataHubMcpException(
                    $"The official DataHub MCP Server is missing required tool '{name}'. Check the installed server version and configuration.");
            }
        }
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    internal static bool ContainsPersistedValue(string verifiedText, string expected)
    {
        if (verifiedText.Contains(expected, StringComparison.Ordinal))
        {
            return true;
        }

        string encoded = JsonSerializer.Serialize(expected);
        return encoded.Length >= 2 &&
            verifiedText.Contains(encoded[1..^1], StringComparison.Ordinal);
    }

    private static string? FindFirstDatasetUrn(string text)
    {
        Match match = DatasetUrnRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    private static string? FindFirstDocumentUrn(string text)
    {
        Match match = DocumentUrnRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    private static async Task<StructuredResponseResult> GenerateAsync(
        StructuredResponseRequest request,
        GeminiConfiguration configuration,
        string grounding,
        CancellationToken cancellationToken)
    {
        OpenAiFailure? lastTemporaryFailure = null;
        foreach (string apiKey in configuration.ApiKeys)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(configuration.Model)}:generateContent");
            message.Headers.Add("x-goog-api-key", apiKey);
            using JsonDocument schema = JsonDocument.Parse(request.JsonSchema);
            string instruction = string.IsNullOrEmpty(grounding)
                ? request.SystemInstruction
                : $"{request.SystemInstruction}\n\nYou are grounded by the official DataHub MCP Server. Use only the supplied organizational metadata as factual evidence. Explicitly identify the resolved dataset, relevant schema fields, owner, upstream sources, and downstream blast radius when present. If the selected SQL references a field absent from the DataHub schema, correct it using only fields present in DataHub. Never invent an owner, field, or lineage relationship.\n<datahub_mcp_context>{grounding}</datahub_mcp_context>";
            object payload = new
            {
                systemInstruction = new { parts = new[] { new { text = instruction } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = request.UserContent } } } },
                generationConfig = new
                {
                    responseMimeType = "application/json",
                    responseJsonSchema = schema.RootElement,
                },
            };
            message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            try
            {
                using HttpResponseMessage response = await Http.SendAsync(message, timeout.Token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    OpenAiFailure failure = Classify(response.StatusCode);
                    if (failure.Retryable)
                    {
                        lastTemporaryFailure = failure;
                        continue;
                    }
                    return StructuredResponseResult.Failed(failure);
                }

                using JsonDocument document = JsonDocument.Parse(body);
                string? json = document.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return StructuredResponseResult.Failed(new OpenAiFailure(
                        OpenAiFailureKind.MalformedResponse,
                        "Gemini returned an empty structured response.",
                        false));
                }
                using JsonDocument validationDocument = JsonDocument.Parse(json);
                return StructuredResponseResult.Success(json, configuration.Model, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastTemporaryFailure = new OpenAiFailure(
                    OpenAiFailureKind.Timeout,
                    "The Gemini request timed out.",
                    true);
            }
        }

        return StructuredResponseResult.Failed(lastTemporaryFailure ?? new OpenAiFailure(
            OpenAiFailureKind.Authentication,
            "GEMINI_API_KEY is missing or invalid.",
            false));
    }

    private static OpenAiFailure Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new(OpenAiFailureKind.Authentication, "Gemini rejected the configured API key.", false),
        HttpStatusCode.TooManyRequests =>
            new(OpenAiFailureKind.RateLimit, "Gemini is temporarily rate limited.", true),
        HttpStatusCode.NotFound =>
            new(OpenAiFailureKind.ModelUnavailable, "The configured Gemini model is unavailable.", false),
        _ when (int)status >= 500 =>
            new(OpenAiFailureKind.Network, "Gemini is temporarily unavailable.", true),
        _ => new(OpenAiFailureKind.Unknown, "Gemini could not complete the request.", false),
    };

    internal static IReadOnlyList<string> ExtractDatasetReferences(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        var references = new List<string>();
        foreach (Match match in DatasetReference().Matches(sql))
        {
            string value = match.Groups[1].Value
                .Trim()
                .Replace("`", string.Empty, StringComparison.Ordinal)
                .Replace("\"", string.Empty, StringComparison.Ordinal)
                .Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(value) &&
                !references.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                references.Add(value);
            }
        }
        return references;
    }

    internal void ApplyGroundingOutcome(bool succeeded, string? datasetUrn, string? datasetName)
    {
        if (!succeeded || string.IsNullOrWhiteSpace(datasetUrn))
        {
            ClearGroundingTarget();
            return;
        }

        lock (_groundingGate)
        {
            _lastGroundedDatasetUrn = datasetUrn;
            _lastGroundedDatasetName = datasetName;
        }
    }

    private void ClearGroundingTarget()
    {
        lock (_groundingGate)
        {
            _lastGroundedDatasetUrn = null;
            _lastGroundedDatasetName = null;
        }
    }

    [GeneratedRegex(
        "(?:\\bfrom|\\bjoin)\\s+([`\"\\[]?[A-Za-z_][A-Za-z0-9_$-]*(?:\\.[A-Za-z_][A-Za-z0-9_$-]*){0,2}[`\"\\]]?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatasetReference();

    [GeneratedRegex("urn:li:dataset:\\([^\\r\\n\"']+\\)", RegexOptions.CultureInvariant)]
    private static partial Regex DatasetUrnRegex();

    [GeneratedRegex("urn:li:document:[A-Za-z0-9._:-]+", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentUrnRegex();

    private sealed record GroundingResult(string Context, string? DatasetUrn, string? DatasetName)
    {
        public static GroundingResult Empty { get; } = new(string.Empty, null, null);
    }

    private sealed record GeminiConfiguration(
        IReadOnlyList<string> ApiKeys,
        string Model,
        string? DataHubGmsUrl,
        string? DataHubToken)
    {
        public static GeminiConfiguration FromEnvironment(string requestedModel)
        {
            var keys = new List<string>();
            Add(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
            foreach (string item in (Environment.GetEnvironmentVariable("GEMINI_API_KEYS") ?? string.Empty)
                         .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(item);
            }
            if (keys.Count == 0)
            {
                throw new GeminiGatewayException(new OpenAiFailure(
                    OpenAiFailureKind.Authentication,
                    "GEMINI_API_KEY is not configured.",
                    false));
            }

            string? model = Environment.GetEnvironmentVariable("GEMINI_MODEL")?.Trim();
            string? gmsUrl = Environment.GetEnvironmentVariable("DATAHUB_GMS_URL")?.Trim();
            if (string.IsNullOrWhiteSpace(gmsUrl))
            {
                string? graphQl = Environment.GetEnvironmentVariable("DATAHUB_GRAPHQL_URL")?.Trim();
                const string suffix = "/api/graphql";
                if (!string.IsNullOrWhiteSpace(graphQl) && graphQl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    gmsUrl = graphQl[..^suffix.Length];
                }
            }
            string? dataHubToken = Environment.GetEnvironmentVariable("DATAHUB_GMS_TOKEN")?.Trim();
            if (string.IsNullOrWhiteSpace(dataHubToken))
            {
                dataHubToken = Environment.GetEnvironmentVariable("DATAHUB_TOKEN")?.Trim();
            }

            return new GeminiConfiguration(
                keys,
                string.IsNullOrWhiteSpace(model) ? requestedModel : model,
                gmsUrl,
                dataHubToken);

            void Add(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) && !keys.Contains(value.Trim(), StringComparer.Ordinal))
                {
                    keys.Add(value.Trim());
                }
            }
        }
    }

    private sealed class GeminiGatewayException(OpenAiFailure failure) : Exception(failure.SafeMessage)
    {
        public OpenAiFailure Failure { get; } = failure;
    }
}

public sealed record DataHubResolutionSaveResult(bool IsSuccess, string? DocumentUrn, string Message)
{
    public static DataHubResolutionSaveResult Success(string documentUrn) =>
        new(true, documentUrn, $"Saved through DataHub MCP and verified by read-after-write as {documentUrn}.");

    public static DataHubResolutionSaveResult Failed(string message) => new(false, null, message);
}