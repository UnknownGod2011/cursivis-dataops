using Cursivis.Contracts;
using Cursivis.Contracts.Browser;

namespace Cursivis.Infrastructure.BrowserBridge;

public sealed class BrowserEnvelopeValidator(TimeProvider? timeProvider = null)
{
    private static readonly HashSet<string> KnownTypes =
    [
        BrowserMessageTypes.Hello,
        BrowserMessageTypes.Welcome,
        BrowserMessageTypes.GetSelection,
        BrowserMessageTypes.Selection,
        BrowserMessageTypes.DiscoverForm,
        BrowserMessageTypes.Form,
        BrowserMessageTypes.Execute,
        BrowserMessageTypes.StepResult,
        BrowserMessageTypes.Health,
        BrowserMessageTypes.Error,
    ];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public BrowserValidationResult Validate(BrowserEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.ProtocolVersion != ProtocolVersions.BrowserBridge)
        {
            return BrowserValidationResult.Fail("unsupported_protocol", "The browser bridge protocol version is not supported.");
        }

        if (!KnownTypes.Contains(envelope.Type))
        {
            return BrowserValidationResult.Fail("unknown_message_type", "The browser bridge message type is not supported.");
        }

        if (string.IsNullOrWhiteSpace(envelope.CorrelationId)
            || envelope.CorrelationId.Length > BrowserEnvelope.MaxCorrelationIdLength
            || envelope.CorrelationId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-')))
        {
            return BrowserValidationResult.Fail("invalid_correlation_id", "The browser bridge correlation identifier is invalid.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if ((now - envelope.SentAt).Duration() > BrowserBridgeLimits.MaximumClockSkew)
        {
            return BrowserValidationResult.Fail("stale_message", "The browser bridge message is outside the permitted time window.");
        }

        if (envelope.Payload.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return BrowserValidationResult.Fail("invalid_payload", "The browser bridge payload must be an object.");
        }

        return BrowserValidationResult.Success;
    }
}

public sealed record BrowserValidationResult(bool IsValid, string? ErrorCode, string? SafeMessage)
{
    public static BrowserValidationResult Success { get; } = new(true, null, null);

    public static BrowserValidationResult Fail(string errorCode, string safeMessage) =>
        new(false, errorCode, safeMessage);
}
