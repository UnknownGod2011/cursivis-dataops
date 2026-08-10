using System.Buffers.Binary;
using System.Text.Json;
using Cursivis.Contracts.Browser;

namespace Cursivis.Infrastructure.BrowserBridge;

public static class NativeMessagingFraming
{
    public static async ValueTask<BrowserEnvelope?> ReadEnvelopeAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        byte[] header = new byte[sizeof(int)];
        int headerBytes = await ReadAtMostAsync(input, header, cancellationToken).ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != header.Length)
        {
            throw new InvalidDataException("Native Messaging frame ended inside its length header.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > BrowserBridgeLimits.MaximumFrameBytes)
        {
            throw new InvalidDataException("Native Messaging frame length is outside the permitted range.");
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        await input.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        BrowserEnvelope? envelope = JsonSerializer.Deserialize<BrowserEnvelope>(payload, BrowserJson.SerializerOptions);
        return envelope ?? throw new InvalidDataException("Native Messaging frame contained JSON null.");
    }

    public static async ValueTask WriteEnvelopeAsync(
        Stream output,
        BrowserEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(envelope);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, BrowserJson.SerializerOptions);
        if (payload.Length == 0 || payload.Length > BrowserBridgeLimits.MaximumFrameBytes)
        {
            throw new InvalidDataException("Native Messaging frame length is outside the permitted range.");
        }

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await input.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
