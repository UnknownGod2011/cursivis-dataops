using System.Text.Json;
using Cursivis.Contracts.Browser;
using Cursivis.Infrastructure.BrowserBridge;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    await using Stream input = Console.OpenStandardInput();
    await using Stream output = Console.OpenStandardOutput();
    using CancellationTokenSource shutdown = new();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    try
    {
        string originExtensionId = ParseOriginExtensionId(arguments);
        BrowserEnvelope firstEnvelope = await NativeMessagingFraming.ReadEnvelopeAsync(input, shutdown.Token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The extension closed before sending its handshake.");

        BrowserEnvelopeValidator envelopeValidator = new();
        BrowserValidationResult envelopeValidation = envelopeValidator.Validate(firstEnvelope);
        if (!envelopeValidation.IsValid || !string.Equals(firstEnvelope.Type, BrowserMessageTypes.Hello, StringComparison.Ordinal))
        {
            await WriteSafeErrorAsync(
                output,
                firstEnvelope.CorrelationId,
                envelopeValidation.ErrorCode ?? "handshake_required",
                envelopeValidation.SafeMessage ?? "A valid browser handshake is required.",
                shutdown.Token).ConfigureAwait(false);
            return 2;
        }

        BrowserHello? hello = firstEnvelope.Payload.Deserialize<BrowserHello>(BrowserJson.SerializerOptions);
        if (hello is null || !string.Equals(hello.ExtensionId, originExtensionId, StringComparison.Ordinal))
        {
            await WriteSafeErrorAsync(
                output,
                firstEnvelope.CorrelationId,
                "origin_mismatch",
                "The native host origin does not match the extension handshake.",
                shutdown.Token).ConfigureAwait(false);
            return 3;
        }

        BrowserPipeClient client = new(BrowserPipeNames.ForCurrentUser());
        await client.RelayAsync(
            input,
            output,
            firstEnvelope,
            TimeSpan.FromSeconds(2),
            shutdown.Token).ConfigureAwait(false);
        return 0;
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
        return 0;
    }
    catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or TimeoutException)
    {
        await TryWriteSafeErrorAsync(output, exception, shutdown.Token).ConfigureAwait(false);
        return 1;
    }
}

static string ParseOriginExtensionId(IEnumerable<string> arguments)
{
    string? origin = arguments.FirstOrDefault(argument =>
        argument.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
    if (origin is null || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
    {
        throw new InvalidDataException("The browser did not provide a valid extension origin.");
    }

    string extensionId = uri.Host;
    if (extensionId.Length != 32 || extensionId.Any(character => character is < 'a' or > 'p'))
    {
        throw new InvalidDataException("The browser extension origin identifier is invalid.");
    }

    return extensionId;
}

static async Task TryWriteSafeErrorAsync(Stream output, Exception exception, CancellationToken cancellationToken)
{
    string code = exception switch
    {
        TimeoutException => "desktop_unavailable",
        IOException => "bridge_unavailable",
        _ => "invalid_native_message",
    };

    string message = code == "invalid_native_message"
        ? "The browser message could not be validated."
        : "Cursivis is not accepting browser connections right now.";

    try
    {
        await WriteSafeErrorAsync(output, "native-host", code, message, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception writeException) when (writeException is IOException or OperationCanceledException)
    {
        // There is no safe recovery channel after the browser closes stdout.
    }
}

static ValueTask WriteSafeErrorAsync(
    Stream output,
    string correlationId,
    string code,
    string message,
    CancellationToken cancellationToken)
{
    BrowserEnvelope envelope = BrowserEnvelope.Create(
        BrowserMessageTypes.Error,
        string.IsNullOrWhiteSpace(correlationId) ? "native-host" : correlationId,
        new BrowserError(code, message, code is "desktop_unavailable" or "bridge_unavailable"));
    return NativeMessagingFraming.WriteEnvelopeAsync(output, envelope, cancellationToken);
}
