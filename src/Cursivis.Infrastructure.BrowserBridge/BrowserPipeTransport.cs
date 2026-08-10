using System.IO.Pipes;
using Cursivis.Contracts.Browser;

namespace Cursivis.Infrastructure.BrowserBridge;

public sealed class BrowserPipeClient(string pipeName)
{
    private readonly string _pipeName = string.IsNullOrWhiteSpace(pipeName)
        ? throw new ArgumentException("A pipe name is required.", nameof(pipeName))
        : pipeName;

    public async Task RelayAsync(
        Stream nativeInput,
        Stream nativeOutput,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        await RelayAsync(nativeInput, nativeOutput, null, connectTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task RelayAsync(
        Stream nativeInput,
        Stream nativeOutput,
        BrowserEnvelope? firstEnvelope,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        using NamedPipeClientStream pipe = new(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectTimeout);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

        if (firstEnvelope is not null)
        {
            await NativeMessagingFraming.WriteEnvelopeAsync(pipe, firstEnvelope, cancellationToken).ConfigureAwait(false);
        }

        Task inbound = PumpAsync(nativeInput, pipe, cancellationToken);
        Task outbound = PumpAsync(pipe, nativeOutput, cancellationToken);
        await Task.WhenAny(inbound, outbound).ConfigureAwait(false);
    }

    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            BrowserEnvelope? envelope = await NativeMessagingFraming.ReadEnvelopeAsync(source, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                break;
            }

            await NativeMessagingFraming.WriteEnvelopeAsync(destination, envelope, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class BrowserPipeServer(string pipeName)
{
    private readonly string _pipeName = string.IsNullOrWhiteSpace(pipeName)
        ? throw new ArgumentException("A pipe name is required.", nameof(pipeName))
        : pipeName;

    public NamedPipeServerStream CreateServer() => new(
        _pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly,
        BrowserBridgeLimits.MaximumFrameBytes,
        BrowserBridgeLimits.MaximumFrameBytes);
}
