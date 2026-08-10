using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Models;
using Cursivis.Infrastructure.OpenAI;

namespace Cursivis.IntegrationTests.OpenAI;

public sealed class OpenAiTranscriptionGatewayTests
{
    [Fact]
    public async Task RejectsRealtimeModelBeforeCredentialAccess()
    {
        var credential = new RecordingCredentialSource();
        var gateway = new OpenAiTranscriptionGateway(credential);
        var request = new TranscriptionRequest(
            ModelCatalog.Realtime.Value,
            new byte[64],
            "audio/wav",
            null,
            TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.TranscribeAsync(request));

        Assert.Equal(0, credential.UseCount);
    }

    [Fact]
    public async Task MissingCredentialReturnsTypedAuthenticationFailure()
    {
        var gateway = new OpenAiTranscriptionGateway(new MissingCredentialSource());
        var request = new TranscriptionRequest(
            ModelCatalog.EconomyTranscription.Value,
            new byte[64],
            "audio/wav",
            null,
            TimeSpan.FromSeconds(30));

        TranscriptionResult result = await gateway.TranscribeAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal(OpenAiFailureKind.Authentication, result.Failure?.Kind);
        Assert.False(result.Failure?.Retryable);
    }

    [Fact]
    public async Task RejectsEmptyOrUnsupportedAudioBeforeCredentialAccess()
    {
        var credential = new RecordingCredentialSource();
        var gateway = new OpenAiTranscriptionGateway(credential);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.TranscribeAsync(
            new TranscriptionRequest(
                ModelCatalog.EconomyTranscription.Value,
                ReadOnlyMemory<byte>.Empty,
                "audio/wav",
                null,
                TimeSpan.FromSeconds(30))));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.TranscribeAsync(
            new TranscriptionRequest(
                ModelCatalog.EconomyTranscription.Value,
                new byte[64],
                "audio/pcm",
                null,
                TimeSpan.FromSeconds(30))));

        Assert.Equal(0, credential.UseCount);
    }

    private sealed class RecordingCredentialSource : IOpenAiCredentialSource
    {
        public int UseCount { get; private set; }

        public Task<T> UseApiKeyAsync<T>(
            Func<string, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            UseCount++;
            throw new InvalidOperationException("Credential access was not expected.");
        }
    }

    private sealed class MissingCredentialSource : IOpenAiCredentialSource
    {
        public Task<T> UseApiKeyAsync<T>(
            Func<string, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            throw new OpenAiCredentialUnavailableException();
    }
}
