using System.ClientModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cursivis.Application.Context;
using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Models;
using OpenAI.Responses;

namespace Cursivis.Infrastructure.OpenAI;

public sealed partial class OpenAiResponsesGateway(
    IOpenAiCredentialSource credentialSource,
    TimeProvider? timeProvider = null) : IResponsesGateway
{
    private const int MaximumInstructionCharacters = 20_000;
    private const int MaximumInputCharacters = 100_000;
    private const int MaximumSchemaCharacters = 100_000;
    private static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromMinutes(2);

    private readonly IOpenAiCredentialSource _credentialSource = credentialSource
        ?? throw new ArgumentNullException(nameof(credentialSource));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly OpenAiRetryPolicy _retryPolicy = new();

    public async Task<StructuredResponseResult> CreateStructuredResponseAsync(
        StructuredResponseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        try
        {
            return await _credentialSource.UseApiKeyAsync(
                (apiKey, keyCancellationToken) => ExecuteWithRetryAsync(
                    apiKey,
                    request,
                    keyCancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OpenAiCredentialUnavailableException)
        {
            return StructuredResponseResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Authentication,
                "An OpenAI API key has not been configured.",
                false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StructuredResponseResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Cancelled,
                "The OpenAI request was cancelled.",
                false));
        }
    }

    private async Task<StructuredResponseResult> ExecuteWithRetryAsync(
        string apiKey,
        StructuredResponseRequest request,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            StructuredResponseResult result = await ExecuteAsync(
                apiKey,
                request,
                cancellationToken).ConfigureAwait(false);
            if (!_retryPolicy.ShouldRetry(result.Failure, attempt))
            {
                return result;
            }

            await Task.Delay(
                _retryPolicy.GetDelay(request.OperationId, attempt),
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ModelAvailabilityResult> CheckModelAvailabilityAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        CursivisModelCatalog.GetRequired(model);
        StructuredResponseRequest request = new(
            model,
            "Return the requested readiness object. Do not add commentary.",
            "Return an object whose ok property is true.",
            "cursivis_model_readiness",
            """
            {
              "type": "object",
              "properties": { "ok": { "const": true } },
              "required": ["ok"],
              "additionalProperties": false
            }
            """,
            TimeSpan.FromSeconds(20));

        StructuredResponseResult result = await CreateStructuredResponseAsync(request, cancellationToken).ConfigureAwait(false);
        return new ModelAvailabilityResult(model, result.Succeeded, result.Failure, _timeProvider.GetUtcNow());
    }

    private static async Task<StructuredResponseResult> ExecuteAsync(
        string apiKey,
        StructuredResponseRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new OpenAiCredentialUnavailableException();
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        try
        {
            ResponsesClient client = new(apiKey);
            List<ResponseContentPart> userContent =
            [
                ResponseContentPart.CreateInputTextPart(request.UserContent),
            ];
            if (request.Image is not null)
            {
                BinaryData image = BinaryData.FromBytes(
                    request.Image.EncodedBytes,
                    request.Image.MediaType);
                userContent.Add(ResponseContentPart.CreateInputImagePart(
                    image,
                    ResponseImageDetailLevel.High));
            }

            CreateResponseOptions options = new(
                request.Model,
                [
                    ResponseItem.CreateDeveloperMessageItem(request.SystemInstruction),
                    ResponseItem.CreateUserMessageItem(userContent),
                ])
            {
                StoredOutputEnabled = false,
                MaxOutputTokenCount = 8_192,
                TextOptions = new ResponseTextOptions
                {
                    TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                        request.SchemaName,
                        BinaryData.FromString(request.JsonSchema),
                        "Cursivis validated structured response",
                        jsonSchemaIsStrict: true),
                },
            };

            ClientResult<ResponseResult> result = await client.CreateResponseAsync(options, timeout.Token).ConfigureAwait(false);
            ResponseResult response = result.Value;
            string? output = response.GetOutputText();
            if (string.IsNullOrWhiteSpace(output) || !IsJsonObject(output))
            {
                return StructuredResponseResult.Failed(new OpenAiFailure(
                    OpenAiFailureKind.MalformedResponse,
                    "OpenAI returned a response that did not match the required structure.",
                    false,
                    response.Id));
            }

            return StructuredResponseResult.Success(output, response.Model, response.Id);
        }
        catch (ClientResultException exception)
        {
            return StructuredResponseResult.Failed(OpenAiFailureClassifier.Classify(exception));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StructuredResponseResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Timeout,
                "The OpenAI request timed out.",
                true));
        }
        catch (HttpRequestException)
        {
            return StructuredResponseResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Network,
                "Cursivis could not reach OpenAI.",
                true));
        }
        catch (JsonException)
        {
            return StructuredResponseResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.MalformedResponse,
                "OpenAI returned malformed structured data.",
                false));
        }
    }

    private static void ValidateRequest(StructuredResponseRequest request)
    {
        OpenAiModelDescriptor descriptor = CursivisModelCatalog.GetRequired(request.Model);
        if (!descriptor.Model.Capabilities.HasFlag(ModelCapabilities.StructuredOutputs))
        {
            throw new ArgumentException("The selected model does not support Structured Outputs.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.SystemInstruction)
            || request.SystemInstruction.Length > MaximumInstructionCharacters)
        {
            throw new ArgumentException("The system instruction is empty or too large.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.UserContent) || request.UserContent.Length > MaximumInputCharacters)
        {
            throw new ArgumentException("The user input is empty or too large.", nameof(request));
        }

        if (request.Image is not null)
        {
            if (request.Image.EncodedBytes.IsEmpty ||
                request.Image.EncodedBytes.Length > ContextImagePayload.MaximumEncodedBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "The image input is empty or too large.");
            }

            if (request.Image.MediaType is not ("image/png" or "image/jpeg" or "image/webp"))
            {
                throw new ArgumentException("The image input media type is unsupported.", nameof(request));
            }
        }

        if (!SchemaNamePattern().IsMatch(request.SchemaName))
        {
            throw new ArgumentException("The schema name is invalid.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.JsonSchema) || request.JsonSchema.Length > MaximumSchemaCharacters)
        {
            throw new ArgumentException("The JSON schema is empty or too large.", nameof(request));
        }

        using JsonDocument schema = JsonDocument.Parse(request.JsonSchema);
        if (schema.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The JSON schema root must be an object.", nameof(request));
        }

        if (request.Timeout < TimeSpan.FromSeconds(1) || request.Timeout > MaximumRequestTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The request timeout is outside the supported range.");
        }
    }

    private static bool IsJsonObject(string value)
    {
        using JsonDocument document = JsonDocument.Parse(value);
        return document.RootElement.ValueKind == JsonValueKind.Object;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNamePattern();
}
