namespace Cursivis.Contracts.OpenAI;

public enum OpenAiFailureKind
{
    Authentication,
    Permission,
    ModelUnavailable,
    Quota,
    RateLimit,
    Network,
    Timeout,
    MalformedResponse,
    SafetyRefusal,
    Cancelled,
    Unknown,
}

public sealed record OpenAiFailure(
    OpenAiFailureKind Kind,
    string SafeMessage,
    bool Retryable,
    string? RequestId = null);

public sealed record ResponseImageInput(
    ReadOnlyMemory<byte> EncodedBytes,
    string MediaType);

public sealed record StructuredResponseRequest(
    string Model,
    string SystemInstruction,
    string UserContent,
    string SchemaName,
    string JsonSchema,
    TimeSpan Timeout,
    string? OperationId = null,
    ResponseImageInput? Image = null);

public sealed record StructuredResponseResult(
    bool Succeeded,
    string? Json,
    string? Model,
    string? ResponseId,
    OpenAiFailure? Failure)
{
    public static StructuredResponseResult Success(string json, string model, string? responseId) =>
        new(true, json, model, responseId, null);

    public static StructuredResponseResult Failed(OpenAiFailure failure) =>
        new(false, null, null, null, failure);
}

public sealed record ModelAvailabilityResult(
    string Model,
    bool Available,
    OpenAiFailure? Failure,
    DateTimeOffset CheckedAt);

public sealed record TranscriptionRequest(
    string Model,
    ReadOnlyMemory<byte> Audio,
    string MediaType,
    string? Language,
    TimeSpan Timeout);

public sealed record TranscriptionResult(
    bool Succeeded,
    string? Text,
    OpenAiFailure? Failure)
{
    public static TranscriptionResult Success(string text) =>
        new(true, text, null);

    public static TranscriptionResult Failed(OpenAiFailure failure) =>
        new(false, null, failure);
}

public enum RealtimeClientEventKind
{
    AudioChunk,
    CommitAudio,
    CancelResponse,
    ToolResult,
    Close,
}

public enum RealtimeServerEventKind
{
    Connected,
    SpeechStarted,
    SpeechStopped,
    UserTranscriptDelta,
    UserTranscriptDone,
    AssistantTranscriptDelta,
    AssistantAudioDelta,
    ToolCall,
    ResponseDone,
    Error,
    Closed,
}

public sealed record RealtimeClientEvent(
    RealtimeClientEventKind Kind,
    ReadOnlyMemory<byte> Audio,
    string? CallId,
    string? Json);

public sealed record RealtimeServerEvent(
    RealtimeServerEventKind Kind,
    string? Text,
    ReadOnlyMemory<byte> Audio,
    string? ToolName,
    string? CallId,
    string? UntrustedArgumentsJson,
    OpenAiFailure? Failure);
