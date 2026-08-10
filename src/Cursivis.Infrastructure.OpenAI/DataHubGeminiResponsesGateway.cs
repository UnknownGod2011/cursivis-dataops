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
/// grounded answer.
/// </summary>
public sealed partial class DataHubGeminiResponsesGateway : IResponsesGateway
{
    private const int MaximumGroundingCharacters = 30_000;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

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

    public async Task<ModelAvailabilityResult> CheckModelAvailabilityAsync(string model, CancellationToken cancellationToken = default)
    {
        GeminiConfiguration configuration = GeminiConfiguration.FromEnvironment(model);
        var request = new StructuredResponseRequest(model, "Return JSON only.", "{\"ok\":true}", "readiness", "{\"type\":\"object\"}", TimeSpan.FromSeconds(20));
        StructuredResponseResult result = await GenerateAsync(request, configuration, string.Empty, cancellationToken).ConfigureAwait(false);
        return new ModelAvailabilityResult(model, result.Succeeded, result.Failure, DateTimeOffset.UtcNow);
    }

    private static async Task<string> GetGroundingAsync(string selectedText, GeminiConfiguration configuration, CancellationToken cancellationToken)
    {
        Match match = DatasetReference().Match(selectedText);
        if (!match.Success)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(configuration.DataHubGraphQlUrl))
        {
            throw new GeminiGatewayException(new OpenAiFailure(OpenAiFailureKind.Network, "DataHub context unavailable — configure DATAHUB_GRAPHQL_URL for grounded data work.", false));
        }

        string dataset = match.Groups[1].Value;
        const string query = "query Search($input: SearchInput!) { search(input: $input) { searchResults { entity { urn type ... on Dataset { name platform { name } properties { description } schemaMetadata { fields { fieldPath nativeDataType description } } ownership { owners { type owner { urn username } } } } } } } }";
        object payload = new { query, variables = new { input = new { type = "DATASET", query = dataset, start = 0, count = 5 } } };
        using var message = new HttpRequestMessage(HttpMethod.Post, configuration.DataHubGraphQlUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(configuration.DataHubToken))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.DataHubToken);
        }

        using HttpResponseMessage response = await Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GeminiGatewayException(new OpenAiFailure(OpenAiFailureKind.Network, "DataHub context unavailable — cannot provide a grounded answer.", response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError));
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("errors", out _) || !body.Contains("searchResults", StringComparison.Ordinal) || !body.Contains("urn", StringComparison.Ordinal))
        {
            throw new GeminiGatewayException(new OpenAiFailure(OpenAiFailureKind.ModelUnavailable, "DataHub found no dataset matching the selected SQL; cannot provide a grounded answer.", false));
        }

        string? urn = FindFirstUrn(document.RootElement);
        string lineage = string.Empty;
        if (!string.IsNullOrWhiteSpace(urn))
        {
            const string lineageQuery = "query Lineage($input: LineageInput!) { lineage(input: $input) { relationships { entity { urn type } } } }";
            object lineagePayload = new { query = lineageQuery, variables = new { input = new { urn, direction = "DOWNSTREAM", start = 0, count = 20 } } };
            using var lineageMessage = new HttpRequestMessage(HttpMethod.Post, configuration.DataHubGraphQlUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(lineagePayload), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(configuration.DataHubToken)) lineageMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.DataHubToken);
            using HttpResponseMessage lineageResponse = await Http.SendAsync(lineageMessage, cancellationToken).ConfigureAwait(false);
            if (lineageResponse.IsSuccessStatusCode) lineage = await lineageResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        string combined = string.IsNullOrWhiteSpace(lineage) ? body : $"{{\"entity\":{body},\"downstreamLineage\":{lineage}}}";
        return combined.Length <= MaximumGroundingCharacters ? combined : combined[..MaximumGroundingCharacters];
    }

    private static async Task<StructuredResponseResult> GenerateAsync(StructuredResponseRequest request, GeminiConfiguration configuration, string grounding, CancellationToken cancellationToken)
    {
        OpenAiFailure? lastTemporaryFailure = null;
        foreach (string apiKey in configuration.ApiKeys)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);
            using var message = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(configuration.Model)}:generateContent");
            message.Headers.Add("x-goog-api-key", apiKey);
            using JsonDocument schema = JsonDocument.Parse(request.JsonSchema);
            string instruction = string.IsNullOrEmpty(grounding)
                ? request.SystemInstruction
                : $"{request.SystemInstruction}\n\nYou are DataHub-grounded. Use only this organizational metadata as evidence; mention the dataset, owner, relevant schema fields, and downstream impact when present.\n<datahub_context>{grounding}</datahub_context>";
            object payload = new
            {
                systemInstruction = new { parts = new[] { new { text = instruction } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = request.UserContent } } } },
                generationConfig = new { responseMimeType = "application/json", responseJsonSchema = schema.RootElement },
            };
            message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            try
            {
                using HttpResponseMessage response = await Http.SendAsync(message, timeout.Token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    OpenAiFailure failure = Classify(response.StatusCode);
                    if (failure.Retryable) { lastTemporaryFailure = failure; continue; }
                    return StructuredResponseResult.Failed(failure);
                }
                using JsonDocument document = JsonDocument.Parse(body);
                string? json = document.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(json)) return StructuredResponseResult.Failed(new OpenAiFailure(OpenAiFailureKind.MalformedResponse, "Gemini returned an empty structured response.", false));
                using JsonDocument validationDocument = JsonDocument.Parse(json);
                return StructuredResponseResult.Success(json, configuration.Model, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastTemporaryFailure = new OpenAiFailure(OpenAiFailureKind.Timeout, "The Gemini request timed out.", true);
            }
        }
        return StructuredResponseResult.Failed(lastTemporaryFailure ?? new OpenAiFailure(OpenAiFailureKind.Authentication, "GEMINI_API_KEY is missing or invalid.", false));
    }

    private static OpenAiFailure Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(OpenAiFailureKind.Authentication, "Gemini rejected the configured API key.", false),
        HttpStatusCode.TooManyRequests => new(OpenAiFailureKind.RateLimit, "Gemini is temporarily rate limited.", true),
        HttpStatusCode.NotFound => new(OpenAiFailureKind.ModelUnavailable, "The configured Gemini model is unavailable.", false),
        _ when (int)status >= 500 => new(OpenAiFailureKind.Network, "Gemini is temporarily unavailable.", true),
        _ => new(OpenAiFailureKind.Unknown, "Gemini could not complete the request.", false),
    };

    private static string? FindFirstUrn(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals("urn") && property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
                string? nested = FindFirstUrn(property.Value);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nested = FindFirstUrn(item);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    [GeneratedRegex("(?:\\bfrom|\\bjoin)\\s+([A-Za-z_][A-Za-z0-9_.-]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatasetReference();

    private sealed record GeminiConfiguration(IReadOnlyList<string> ApiKeys, string Model, string? DataHubGraphQlUrl, string? DataHubToken)
    {
        public static GeminiConfiguration FromEnvironment(string requestedModel)
        {
            var keys = new List<string>();
            Add(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
            foreach (string item in (Environment.GetEnvironmentVariable("GEMINI_API_KEYS") ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Add(item);
            if (keys.Count == 0) throw new GeminiGatewayException(new OpenAiFailure(OpenAiFailureKind.Authentication, "GEMINI_API_KEY is not configured.", false));
            string? model = Environment.GetEnvironmentVariable("GEMINI_MODEL")?.Trim();
            return new GeminiConfiguration(keys, string.IsNullOrWhiteSpace(model) ? requestedModel : model, Environment.GetEnvironmentVariable("DATAHUB_GRAPHQL_URL")?.Trim(), Environment.GetEnvironmentVariable("DATAHUB_TOKEN")?.Trim());
            void Add(string? value) { if (!string.IsNullOrWhiteSpace(value) && !keys.Contains(value.Trim(), StringComparer.Ordinal)) keys.Add(value.Trim()); }
        }
    }

    private sealed class GeminiGatewayException(OpenAiFailure failure) : Exception(failure.SafeMessage)
    {
        public OpenAiFailure Failure { get; } = failure;
    }
}
