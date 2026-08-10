using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Cursivis.Windows.Platform.Instance;

public enum ActivationRequestKind
{
    OpenSettings = 1,
    ShowOverview = 2,
}

public sealed record ActivationRequest(
    int SchemaVersion,
    ActivationRequestKind Kind,
    string CorrelationId,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public static ActivationRequest Create(
        ActivationRequestKind kind,
        TimeSpan? lifetime = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var effectiveLifetime = lifetime ?? TimeSpan.FromSeconds(10);
        if (effectiveLifetime <= TimeSpan.Zero || effectiveLifetime > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var now = DateTimeOffset.UtcNow;
        return new ActivationRequest(
            CurrentSchemaVersion,
            kind,
            Guid.NewGuid().ToString("N"),
            now,
            now.Add(effectiveLifetime));
    }
}

public enum ActivationHandoffStatus
{
    Delivered,
    PrimaryUnavailable,
    TimedOut,
    Rejected,
    HandlerFailed,
}

public sealed record ActivationHandoffResult(ActivationHandoffStatus Status)
{
    public bool Delivered => Status == ActivationHandoffStatus.Delivered;
}

public interface IActivationHandoffClient
{
    Task<ActivationHandoffResult> SendAsync(
        ActivationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface IActivationHandoffServer
{
    Task RunAsync(
        Func<ActivationRequest, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken);
}

public sealed class CurrentUserActivationHandoff : IActivationHandoffClient, IActivationHandoffServer
{
    private const int MaximumMessageBytes = 4 * 1024;
    private const byte Acknowledged = 1;
    private const byte Rejected = 2;
    private const byte HandlerFailed = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _pipeName;

    public CurrentUserActivationHandoff(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pipeName.Length > 200 || pipeName.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("The activation pipe name is invalid.", nameof(pipeName));
        }

        _pipeName = pipeName;
    }

    public async Task<ActivationHandoffResult> SendAsync(
        ActivationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(request);
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (!IsValid(request, DateTimeOffset.UtcNow))
        {
            return new ActivationHandoffResult(ActivationHandoffStatus.Rejected);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var effectiveToken = linkedSource.Token;

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(effectiveToken).ConfigureAwait(false);

            var payload = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);
            try
            {
                if (payload.Length is <= 0 or > MaximumMessageBytes)
                {
                    return new ActivationHandoffResult(ActivationHandoffStatus.Rejected);
                }

                var prefix = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
                await client.WriteAsync(prefix, effectiveToken).ConfigureAwait(false);
                await client.WriteAsync(payload, effectiveToken).ConfigureAwait(false);
                await client.FlushAsync(effectiveToken).ConfigureAwait(false);

                var acknowledgement = new byte[1];
                await client.ReadExactlyAsync(acknowledgement, effectiveToken).ConfigureAwait(false);
                return acknowledgement[0] switch
                {
                    Acknowledged => new ActivationHandoffResult(ActivationHandoffStatus.Delivered),
                    HandlerFailed => new ActivationHandoffResult(ActivationHandoffStatus.HandlerFailed),
                    _ => new ActivationHandoffResult(ActivationHandoffStatus.Rejected),
                };
            }
            finally
            {
                Array.Clear(payload);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ActivationHandoffResult(ActivationHandoffStatus.TimedOut);
        }
        catch (TimeoutException)
        {
            return new ActivationHandoffResult(ActivationHandoffStatus.TimedOut);
        }
        catch (IOException)
        {
            return new ActivationHandoffResult(ActivationHandoffStatus.PrimaryUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return new ActivationHandoffResult(ActivationHandoffStatus.PrimaryUnavailable);
        }
    }

    public async Task RunAsync(
        Func<ActivationRequest, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(handler);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: MaximumMessageBytes,
                    outBufferSize: 16);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ProcessConnectionAsync(server, handler, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // A disconnected or malformed activation request must not stop the primary listener.
            }
        }
    }

    private static async Task ProcessConnectionAsync(
        NamedPipeServerStream server,
        Func<ActivationRequest, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken)
    {
        var acknowledgement = Rejected;
        try
        {
            var prefix = new byte[sizeof(int)];
            await server.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length is <= 0 or > MaximumMessageBytes)
            {
                await WriteAcknowledgementAsync(server, acknowledgement, cancellationToken).ConfigureAwait(false);
                return;
            }

            var payload = new byte[length];
            try
            {
                await server.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<ActivationRequest>(payload, SerializerOptions);
                if (request is null || !IsValid(request, DateTimeOffset.UtcNow))
                {
                    await WriteAcknowledgementAsync(server, acknowledgement, cancellationToken).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await handler(request, cancellationToken).ConfigureAwait(false);
                    acknowledgement = Acknowledged;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsRecoverableHandlerException(exception))
                {
                    acknowledgement = HandlerFailed;
                }
            }
            catch (JsonException)
            {
                acknowledgement = Rejected;
            }
            finally
            {
                Array.Clear(payload);
            }

            await WriteAcknowledgementAsync(server, acknowledgement, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            // The secondary instance disconnected before sending a complete bounded request.
        }
    }

    private static Task WriteAcknowledgementAsync(
        PipeStream server,
        byte acknowledgement,
        CancellationToken cancellationToken) =>
        server.WriteAsync(new[] { acknowledgement }, cancellationToken).AsTask();

    private static bool IsValid(ActivationRequest request, DateTimeOffset now)
    {
        if (request.SchemaVersion != ActivationRequest.CurrentSchemaVersion ||
            !Enum.IsDefined(request.Kind) ||
            !Guid.TryParseExact(request.CorrelationId, "N", out _))
        {
            return false;
        }

        var maximumSkew = TimeSpan.FromMinutes(1);
        return request.RequestedAtUtc <= now.Add(maximumSkew) &&
               request.ExpiresAtUtc > now &&
               request.ExpiresAtUtc <= request.RequestedAtUtc.AddMinutes(1);
    }

    private static bool IsRecoverableHandlerException(Exception exception) =>
        exception is not OutOfMemoryException &&
        exception is not StackOverflowException &&
        exception is not AccessViolationException;

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Activation handoff requires Windows named pipes.");
        }
    }
}
