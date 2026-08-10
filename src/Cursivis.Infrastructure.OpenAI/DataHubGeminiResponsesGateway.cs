using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;

namespace Cursivis.Infrastructure.OpenAI;

/// <summary>
/// Gemini implementation for Cursivis' existing structured-response port.
/// For SQL-like selections it resolves catalog context from DataHub before the
/// model is called; a missing catalog is an explicit failure, never a fake
/// grounded answer. Reviewed resolutions can be written back as DataHub context
/// documents only after the UI explicitly asks this gateway to save them.
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
        try
        {
            GeminiConfiguration configuration = GeminiConfiguration.FromEnvironment(request.Model);
            string grounding = await GetGroundingAsync(request.UserContent, configuration, cancellationToken).ConfigureAwait(false);
            return await GenerateAsync(request, configuration, grounding, cancellationToken).ConfigureAwait(false);
        }
        catch (GeminiGatewayException exception)
        {
            return StructuredResponseResult.Failed(exception.Failure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StructuredResponseResult.Failed(new OpenAiFailure(OpenAiFailureKind.Timeout, "The Gemini request timed out.", true));
        }
        catch (HttpRequestException)
        {
            return StructuredResponseResult.Failed(new OpenAiFailure(OpenAiFailureKind.Network, "Cursivis could not reach Gemini or DataHub.", true));
        }
        catch (JsonException)
        {
            return StructuredResponseResult.Failed(new OpenAiFailure(OpenAiFailureKind.MalformedResponse, "Gemini returned malformed structured data.", false));
        }
    }

    public async Task<ModelAvailabilityResult> CheckModelAvailabilityAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        GeminiConfiguration configuration = GeminiConfiguration.FromEnvironment(model);
        var request = new StructuredResponseRequest(
            model,
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
        return new ModelAvailabilityResult(model, result.Succeeded, result.Failure, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Stores a reviewed resolution as a hidden DataHub context document linked
    /// to the most recently grounded dataset, then reads the document back before
    /// returning success. This method never runs automatically.
    /// </summary>
    public async Task<DataHubResolutionSaveResult> SaveResolutionAsync(
        string resolutionText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resolutionText))
        {
            return DataHubResolutionSaveResult.Failed("There is no reviewed resolution to save.");
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
            // Save does not call Gemini, but the same environment configuration
            // owns the DataHub endpoint/token used by the grounded request.
            configuration = GeminiConfiguration.FromEnvironment(
                Environment.GetEnvironmentVariable("GEMINI_MODEL")?.Trim() ?? "gemini-2.5-flash");
        }
        catch (GeminiGatewayException exception)
        {
            return DataHubResolutionSaveResult.Failed(exception.Failure.SafeMessage);
        }

        if (string.IsNullOrWhiteSpace(configuration.DataHubGraphQlUrl))
        {
            return DataHubResolutionSaveResult.Failed(
                "DataHub context unavailable — configure DATAHUB_GRAPHQL_URL before saving a resolution.");
        }

        string trimmed = resolutionText.Trim();
        if (trimmed.Length > MaximumResolutionCharacters)
        {
            trimmed = trimmed[..MaximumResolutionCharacters];
        }

        string id = $"cursivis-resolution-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..48];
        string title = $"Cursivis resolution — {datasetName ?? "DataHub dataset"}";
        const string mutation = "mutation SaveResolution($id: String!, $text: String!, $title: String!, $asset: String!) { createDocument(input: { id: $id, contents: { text: $text }, title: $title, subType: \"Cursivis Resolution\", state: PUBLISHED, settings: { showInGlobalContext: false }, relatedAssets: [$asset] }) }";
        object payload = new
        {
            query = mutation,
            variables = new
            {
                id,
                text = trimmed,
                title,
                asset = datasetUrn,
            },
        };

        try
        {
            using HttpResponseMessage response = await SendDataHubGraphQlAsync(
                configuration,
                payload,
                cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return DataHubResolutionSaveResult.Failed(
                    $"DataHub rejected the resolution write ({(int)response.StatusCode}). No catalog metadata was changed.");
            }

            using JsonDocument document = JsonDocument.Parse(body);
            if (HasGraphQlErrors(document.RootElement))
            {
                return DataHubResolutionSaveResult.Failed(
                    "DataHub rejected the resolution write. No catalog metadata was changed.");
            }

            if (!TryGetStringAtPath(document.RootElement, out string? documentUrn, "data", "createDocument") ||
                string.IsNullOrWhiteSpace(documentUrn))
            {
                return DataHubResolutionSaveResult.Failed(
                    "DataHub did not return the saved document identifier, so Cursivis could not verify the write.");
            }

            return await VerifySavedResolutionAsync(
                configuration,
                documentUrn,
                datasetUrn,
                trimmed,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DataHubResolutionSaveResult.Failed("DataHub timed out while saving the reviewed resolution.");
        }
        catch (HttpRequestException)
        {
            return DataHubResolutionSaveResult.Failed("Cursivis could not reach DataHub to save the reviewed resolution.");
        }
        catch (JsonException)
        {
            return DataHubResolutionSaveResult.Failed("DataHub returned malformed data while saving the reviewed resolution.");
        }
    }

    private async Task<string> GetGroundingAsync(
        string selectedText,
        GeminiConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string? dataset = ExtractDatasetReferences(selectedText).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(dataset))
        {
            ClearGroundingTarget();
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(configuration.DataHubGraphQlUrl))
        {
            ClearGroundingTarget();
            throw new GeminiGatewayException(new OpenAiFailure(
                OpenAiFailureKind.Network,
                "DataHub context unavailable — configure DATAHUB_GRAPHQL_URL for grounded data work.",
                false));
        }

        const string query = "query Search($input: SearchInput!) { search(input: $input) { searchResults { entity { urn type ... on Dataset { name platform { name } properties { description } schemaMetadata { fields { fieldPath nativeDataType description } } ownership { owners { type owner { urn username } } } } } } } }";
        object payload = new
        {
            query,
            variables = new { input = new { type = "DATASET", query = dataset, start = 0, count = 5 } },
        };
        using HttpResponseMessage response = await SendDataHubGraphQlAsync(
            configuration,
            payload,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ClearGroundingTarget();
            throw new GeminiGatewayException(new OpenAiFailure(
                OpenAiFailureKind.Network,
                "DataHub context unavailable — cannot provide a grounded answer.",
                response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError));
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        if (HasGraphQlErrors(document.RootElement) ||
            !body.Contains("searchResults", StringComparison.Ordinal) ||
            !body.Contains("urn", StringComparison.Ordinal))
        {
            ClearGroundingTarget();
            throw new GeminiGatewayException(new OpenAiFailure(
                OpenAiFailureKind.ModelUnavailable,
                "DataHub found no dataset matching the selected SQL; cannot provide a grounded answer.",
                false));
        }

        string? urn = FindFirstUrn(document.RootElement);
        if (string.IsNullOrWhiteSpace(urn))
        {
            ClearGroundingTarget();
            throw new GeminiGatewayException(new OpenAiFailure(
                OpenAiFailureKind.ModelUnavailable,
                "DataHub found no dataset matching the selected SQL; cannot provide a grounded answer.",
                false));
        }

        lock (_groundingGate)
        {
            _lastGroundedDatasetUrn = urn;
            _lastGroundedDatasetName = dataset;
        }

        string downstreamLineage = await GetLineageAsync(
            configuration,
            urn,
            "DOWNSTREAM",
            cancellationToken).ConfigureAwait(false);
        string upstreamLineage = await GetLineageAsync(
            configuration,
            urn,
            "UPSTREAM",
            cancellationToken).ConfigureAwait(false);

        string combined = $"{{\"entity\":{body},\"upstreamLineage\":{NormalizeGraphQlJson(upstreamLineage)},\"downstreamLineage\":{NormalizeGraphQlJson(downstreamLineage)}}}";
        return combined.Length <= MaximumGroundingCharacters
            ? combined
            : combined[..MaximumGroundingCharacters];
    }

    private static async Task<string> GetLineageAsync(
        GeminiConfiguration configuration,
        string urn,
        string direction,
        CancellationToken cancellationToken)
    {
        const string lineageQuery = "query Lineage($input: LineageInput!) { lineage(input: $input) { relationships { entity { urn type } } } }";
        object lineagePayload = new
        {
            query = lineageQuery,
            variables = new { input = new { urn, direction, start = 0, count = 20 } },
        };
        using HttpResponseMessage response = await SendDataHubGraphQlAsync(
            configuration,
            lineagePayload,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return "{}";
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return HasGraphQlErrors(document.RootElement) ? "{}" : body;
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static async Task<DataHubResolutionSaveResult> VerifySavedResolutionAsync(
        GeminiConfiguration configuration,
        string documentUrn,
        string expectedDatasetUrn,
        string expectedText,
        CancellationToken cancellationToken)
    {
        const string query = "query VerifyResolution($urn: String!) { document(urn: $urn) { urn info { title contents { text } relatedAssets { asset { urn } } } settings { showInGlobalContext } } }";
        object payload = new { query, variables = new { urn = documentUrn } };
        using HttpResponseMessage response = await SendDataHubGraphQlAsync(
            configuration,
            payload,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return DataHubResolutionSaveResult.Failed(
                "The resolution was submitted, but Cursivis could not verify it by reading it back from DataHub.");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        if (HasGraphQlErrors(document.RootElement) ||
            !TryGetPropertyAtPath(document.RootElement, out JsonElement savedDocument, "data", "document") ||
            savedDocument.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return DataHubResolutionSaveResult.Failed(
                "The resolution was submitted, but DataHub did not return it during read-after-write verification.");
        }

        if (!TryGetStringAtPath(savedDocument, out string? actualText, "info", "contents", "text") ||
            !string.Equals(actualText, expectedText, StringComparison.Ordinal))
        {
            return DataHubResolutionSaveResult.Failed(
                "DataHub returned the document, but its contents did not match the reviewed resolution.");
        }

        bool relatedAssetVerified = false;
        if (TryGetPropertyAtPath(savedDocument, out JsonElement relatedAssets, "info", "relatedAssets") &&
            relatedAssets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in relatedAssets.EnumerateArray())
            {
                if (TryGetStringAtPath(item, out string? urn, "asset", "urn") &&
                    string.Equals(urn, expectedDatasetUrn, StringComparison.Ordinal))
                {
                    relatedAssetVerified = true;
                    break;
                }
            }
        }

        if (!relatedAssetVerified)
        {
            return DataHubResolutionSaveResult.Failed(
                "DataHub saved the document, but Cursivis could not verify its link to the grounded dataset.");
        }

        return DataHubResolutionSaveResult.Success(documentUrn);
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
                : $"{request.SystemInstruction}\n\nYou are DataHub-grounded. Use only this organizational metadata as evidence. Explicitly identify the resolved dataset, relevant schema fields, owner, upstream sources, and downstream impact when present. If the selected SQL references a field that is absent from the schema, correct it using only fields present in DataHub.\n<datahub_context>{grounding}</datahub_context>";
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

        return StructuredResponseResult.Failed(
            lastTemporaryFailure ?? new OpenAiFailure(
                OpenAiFailureKind.Authentication,
                "GEMINI_API_KEY is missing or invalid.",
                false));
    }

    private static async Task<HttpResponseMessage> SendDataHubGraphQlAsync(
        GeminiConfiguration configuration,
        object payload,
        CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, configuration.DataHubGraphQlUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(configuration.DataHubToken))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.DataHubToken);
        }

        try
        {
            return await Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            message.Dispose();
        }
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

    private void ClearGroundingTarget()
    {
        lock (_groundingGate)
        {
            _lastGroundedDatasetUrn = null;
            _lastGroundedDatasetName = null;
        }
    }

    private static bool HasGraphQlErrors(JsonElement root) =>
        root.TryGetProperty("errors", out JsonElement errors) &&
        errors.ValueKind == JsonValueKind.Array &&
        errors.GetArrayLength() > 0;

    private static string NormalizeGraphQlJson(string json) =>
        string.IsNullOrWhiteSpace(json) ? "{}" : json;

    private static bool TryGetStringAtPath(
        JsonElement element,
        out string? value,
        params string[] path)
    {
        value = null;
        if (!TryGetPropertyAtPath(element, out JsonElement current, path) ||
            current.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = current.GetString();
        return true;
    }

    private static bool TryGetPropertyAtPath(
        JsonElement element,
        out JsonElement value,
        params string[] path)
    {
        value = element;
        foreach (string segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out JsonElement next))
            {
                value = default;
                return false;
            }

            value = next;
        }

        return true;
    }

    private static string? FindFirstUrn(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals("urn") && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                string? nested = FindFirstUrn(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nested = FindFirstUrn(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    [GeneratedRegex(
        "(?:\\bfrom|\\bjoin)\\s+([`\"\\[]?[A-Za-z_][A-Za-z0-9_$-]*(?:\\.[A-Za-z_][A-Za-z0-9_$-]*){0,2}[`\"\\]]?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatasetReference();

    private sealed record GeminiConfiguration(
        IReadOnlyList<string> ApiKeys,
        string Model,
        string? DataHubGraphQlUrl,
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
            return new GeminiConfiguration(
                keys,
                string.IsNullOrWhiteSpace(model) ? requestedModel : model,
                Environment.GetEnvironmentVariable("DATAHUB_GRAPHQL_URL")?.Trim(),
                Environment.GetEnvironmentVariable("DATAHUB_TOKEN")?.Trim());

            void Add(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !keys.Contains(value.Trim(), StringComparer.Ordinal))
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

public sealed record DataHubResolutionSaveResult(
    bool IsSuccess,
    string? DocumentUrn,
    string Message)
{
    public static DataHubResolutionSaveResult Success(string documentUrn) =>
        new(true, documentUrn, $"Saved and verified in DataHub as {documentUrn}.");

    public static DataHubResolutionSaveResult Failed(string message) =>
        new(false, null, message);
}
