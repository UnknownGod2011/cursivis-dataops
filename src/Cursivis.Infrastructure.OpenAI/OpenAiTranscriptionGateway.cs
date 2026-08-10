using System.ClientModel;
using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Models;
using OpenAI.Audio;

namespace Cursivis.Infrastructure.OpenAI;

public sealed class OpenAiTranscriptionGateway(
    IOpenAiCredentialSource credentialSource,
    TimeProvider? timeProvider = null) : ITranscriptionGateway
{
    private const int MaximumAudioBytes = 25 * 1024 * 1024;
    private static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromMinutes(2);
    private readonly IOpenAiCredentialSource _credentialSource = credentialSource
        ?? throw new ArgumentNullException(nameof(credentialSource));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly OpenAiRetryPolicy _retryPolicy = new();

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

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
            return TranscriptionResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Authentication,
                "An OpenAI API key has not been configured.",
                false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TranscriptionResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Cancelled,
                "The OpenAI transcription was cancelled.",
                false));
        }
    }

    private async Task<TranscriptionResult> ExecuteWithRetryAsync(
        string apiKey,
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        string operationId = $"transcription-{request.Audio.Length}-{request.Model}";
        for (int attempt = 1; ; attempt++)
        {
            TranscriptionResult result = await ExecuteAsync(
                apiKey,
                request,
                cancellationToken).ConfigureAwait(false);
            if (!_retryPolicy.ShouldRetry(result.Failure, attempt))
            {
                return result;
            }

            await Task.Delay(
                _retryPolicy.GetDelay(operationId, attempt),
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<TranscriptionResult> ExecuteAsync(
        string apiKey,
        TranscriptionRequest request,
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
            var client = new AudioClient(request.Model, apiKey);
            var options = new AudioTranscriptionOptions
            {
                ResponseFormat = AudioTranscriptionFormat.Simple,
            };
            if (!string.IsNullOrWhiteSpace(request.Language))
            {
                options.Language = request.Language.Trim();
            }

            using var audio = new MemoryStream(request.Audio.ToArray(), writable: false);
            ClientResult<AudioTranscription> result = await client.TranscribeAudioAsync(
                audio,
                FileNameFor(request.MediaType),
                options,
                timeout.Token).ConfigureAwait(false);
            string? text = result.Value.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return TranscriptionResult.Failed(new OpenAiFailure(
                    OpenAiFailureKind.MalformedResponse,
                    "OpenAI returned an empty transcription.",
                    false));
            }

            return TranscriptionResult.Success(text.Trim());
        }
        catch (ClientResultException exception)
        {
            return TranscriptionResult.Failed(OpenAiFailureClassifier.Classify(exception));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TranscriptionResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Timeout,
                "The OpenAI transcription timed out.",
                true));
        }
        catch (HttpRequestException)
        {
            return TranscriptionResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Network,
                "Cursivis could not reach OpenAI.",
                true));
        }
    }

    private static void Validate(TranscriptionRequest request)
    {
        ModelDescriptor descriptor = ModelCatalog.Find(new ModelIdentifier(request.Model))
            ?? throw new ArgumentException("The transcription model is not in the Cursivis model catalog.", nameof(request));
        if (descriptor.Role != ModelRole.Transcription ||
            !descriptor.Capabilities.HasFlag(ModelCapabilities.BoundedTranscription))
        {
            throw new ArgumentException("The selected model does not support bounded transcription.", nameof(request));
        }

        if (request.Audio.IsEmpty || request.Audio.Length > MaximumAudioBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The audio input is empty or too large.");
        }

        if (request.MediaType is not ("audio/wav" or "audio/x-wav" or "audio/mpeg" or "audio/mp4" or "audio/m4a"))
        {
            throw new ArgumentException("The transcription audio type is unsupported.", nameof(request));
        }

        if (request.Timeout < TimeSpan.FromSeconds(1) || request.Timeout > MaximumRequestTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The transcription timeout is outside the supported range.");
        }
    }

    private static string FileNameFor(string mediaType) => mediaType switch
    {
        "audio/wav" or "audio/x-wav" => "cursivis-dictation.wav",
        "audio/mpeg" => "cursivis-dictation.mp3",
        "audio/mp4" or "audio/m4a" => "cursivis-dictation.m4a",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType)),
    };
}
