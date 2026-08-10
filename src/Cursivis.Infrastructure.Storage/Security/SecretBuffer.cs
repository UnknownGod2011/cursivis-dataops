using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Cursivis.Infrastructure.Storage.Security;

public delegate TResult SecretBufferAccessor<TResult>(ReadOnlySpan<char> secret);

public sealed class SecretBuffer : IDisposable
{
    private readonly object _sync = new();
    private char[]? _characters;

    public SecretBuffer(ReadOnlySpan<char> secret)
    {
        if (secret.IsEmpty)
        {
            throw new ArgumentException("A secret cannot be empty.", nameof(secret));
        }

        _characters = secret.ToArray();
    }

    ~SecretBuffer()
    {
        Dispose(disposing: false);
    }

    public bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _characters is null;
            }
        }
    }

    public TResult Use<TResult>(SecretBufferAccessor<TResult> accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_characters is null, this);
            return accessor(_characters.AsSpan());
        }
    }

    public override string ToString() => "[REDACTED]";

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        _ = disposing;

        lock (_sync)
        {
            if (_characters is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(
                MemoryMarshal.AsBytes(_characters.AsSpan()));
            _characters = null;
        }
    }
}
