using Cursivis.Contracts.OpenAI;

namespace Cursivis.Application.OpenAI;

/// <summary>
/// Grants an operation temporary access to the API key without making the key a property,
/// configuration value, log field, or generally retrievable service.
/// </summary>
public interface IOpenAiCredentialSource
{
    Task<T> UseApiKeyAsync<T>(
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAiCredentialUnavailableException : InvalidOperationException
{
    public OpenAiCredentialUnavailableException()
        : base("An OpenAI credential is unavailable.")
    {
    }
}

public interface IResponsesGateway
{
    Task<StructuredResponseResult> CreateStructuredResponseAsync(
        StructuredResponseRequest request,
        CancellationToken cancellationToken = default);

    Task<ModelAvailabilityResult> CheckModelAvailabilityAsync(
        string model,
        CancellationToken cancellationToken = default);
}

public interface ITranscriptionGateway
{
    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRealtimeGateway
{
    Task<IRealtimeSession> ConnectAsync(
        RealtimeSessionOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class RealtimeGatewayException : InvalidOperationException
{
    public RealtimeGatewayException(OpenAiFailure failure, Exception? innerException = null)
        : base(failure?.SafeMessage, innerException)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public OpenAiFailure Failure { get; }
}

public interface IRealtimeSession : IAsyncDisposable
{
    IAsyncEnumerable<RealtimeServerEvent> ReadEventsAsync(CancellationToken cancellationToken = default);

    ValueTask SendAsync(RealtimeClientEvent clientEvent, CancellationToken cancellationToken = default);
}

public sealed record RealtimeSessionOptions(
    string Model,
    string Voice,
    string? Language,
    bool SemanticVad,
    IReadOnlyList<RealtimeToolDefinition> Tools,
    string? ContextInstructions = null);

public sealed record RealtimeToolDefinition(
    string Name,
    string Description,
    string JsonSchema);
