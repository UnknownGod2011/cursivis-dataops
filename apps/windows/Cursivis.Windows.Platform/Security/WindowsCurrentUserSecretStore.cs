using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Cursivis.Infrastructure.Storage.Persistence;
using Cursivis.Infrastructure.Storage.Security;

namespace Cursivis.Windows.Platform.Security;

public interface ICurrentUserSecretStore
{
    Task SaveAsync(
        string logicalName,
        SecretBuffer secret,
        CancellationToken cancellationToken = default);

    Task<SecretBuffer?> ReadAsync(
        string logicalName,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string logicalName,
        CancellationToken cancellationToken = default);

    bool Exists(string logicalName);
}

public enum SecretStoreFailureCode
{
    ProtectionFailed,
    DecryptionFailed,
    CorruptPayload,
    StorageUnavailable,
}

public sealed class SecretStoreException : Exception
{
    public SecretStoreException(SecretStoreFailureCode failureCode)
        : base($"Secure secret storage failed with code {failureCode}.")
    {
        FailureCode = failureCode;
    }

    public SecretStoreFailureCode FailureCode { get; }
}

public sealed record WindowsCurrentUserSecretStoreOptions
{
    public WindowsCurrentUserSecretStoreOptions(string directory, string applicationPurpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPurpose);

        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("The secret-store directory must be absolute.", nameof(directory));
        }

        if (applicationPurpose.Length > 128 || applicationPurpose.Any(char.IsControl))
        {
            throw new ArgumentException("The secret-store purpose is invalid.", nameof(applicationPurpose));
        }

        Directory = Path.GetFullPath(directory);
        ApplicationPurpose = applicationPurpose;
    }

    public string Directory { get; }

    public string ApplicationPurpose { get; }
}

public sealed class WindowsCurrentUserSecretStore : ICurrentUserSecretStore
{
    private const int MaximumPayloadBytes = 1024 * 1024;
    private const byte FormatVersion = 1;
    private static readonly byte[] Magic = "CVSSEC01"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _directory;
    private readonly byte[] _entropy;
    private readonly AtomicFileWriter _writer = new(backupRetentionCount: 0);

    public WindowsCurrentUserSecretStore(WindowsCurrentUserSecretStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = options.Directory;
        _entropy = SHA256.HashData(StrictUtf8.GetBytes(options.ApplicationPurpose));
    }

    public async Task SaveAsync(
        string logicalName,
        SecretBuffer secret,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(secret);
        var path = GetPath(logicalName);

        byte[]? plaintext = null;
        byte[]? protectedBytes = null;
        byte[]? payload = null;
        try
        {
            plaintext = secret.Use(static value =>
            {
                var bytes = new byte[StrictUtf8.GetByteCount(value)];
                _ = StrictUtf8.GetBytes(value, bytes);
                return bytes;
            });
            protectedBytes = ProtectedData.Protect(
                plaintext,
                _entropy,
                DataProtectionScope.CurrentUser);

            payload = new byte[Magic.Length + 1 + sizeof(int) + protectedBytes.Length];
            Magic.CopyTo(payload, 0);
            payload[Magic.Length] = FormatVersion;
            BinaryPrimitives.WriteInt32LittleEndian(
                payload.AsSpan(Magic.Length + 1, sizeof(int)),
                protectedBytes.Length);
            protectedBytes.CopyTo(payload, Magic.Length + 1 + sizeof(int));

            await _writer.WriteAsync(path, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            throw new SecretStoreException(SecretStoreFailureCode.ProtectionFailed);
        }
        catch (IOException)
        {
            throw new SecretStoreException(SecretStoreFailureCode.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            throw new SecretStoreException(SecretStoreFailureCode.StorageUnavailable);
        }
        finally
        {
            Zero(plaintext);
            Zero(protectedBytes);
            Zero(payload);
        }
    }

    public async Task<SecretBuffer?> ReadAsync(
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        var path = GetPath(logicalName);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[]? payload = null;
        byte[]? protectedBytes = null;
        byte[]? plaintext = null;
        char[]? characters = null;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length is <= 0 or > MaximumPayloadBytes)
            {
                throw new SecretStoreException(SecretStoreFailureCode.CorruptPayload);
            }

            payload = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

            var headerBytes = Magic.Length + 1 + sizeof(int);
            if (payload.Length < headerBytes ||
                !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                payload[Magic.Length] != FormatVersion)
            {
                throw new SecretStoreException(SecretStoreFailureCode.CorruptPayload);
            }

            var protectedLength = BinaryPrimitives.ReadInt32LittleEndian(
                payload.AsSpan(Magic.Length + 1, sizeof(int)));
            if (protectedLength <= 0 || protectedLength != payload.Length - headerBytes)
            {
                throw new SecretStoreException(SecretStoreFailureCode.CorruptPayload);
            }

            protectedBytes = payload.AsSpan(headerBytes, protectedLength).ToArray();
            plaintext = ProtectedData.Unprotect(
                protectedBytes,
                _entropy,
                DataProtectionScope.CurrentUser);
            characters = StrictUtf8.GetChars(plaintext);

            if (characters.Length == 0)
            {
                throw new SecretStoreException(SecretStoreFailureCode.CorruptPayload);
            }

            return new SecretBuffer(characters);
        }
        catch (SecretStoreException)
        {
            throw;
        }
        catch (CryptographicException)
        {
            throw new SecretStoreException(SecretStoreFailureCode.DecryptionFailed);
        }
        catch (DecoderFallbackException)
        {
            throw new SecretStoreException(SecretStoreFailureCode.CorruptPayload);
        }
        catch (IOException)
        {
            throw new SecretStoreException(SecretStoreFailureCode.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            throw new SecretStoreException(SecretStoreFailureCode.StorageUnavailable);
        }
        finally
        {
            Zero(payload);
            Zero(protectedBytes);
            Zero(plaintext);
            if (characters is not null)
            {
                CryptographicOperations.ZeroMemory(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(characters.AsSpan()));
            }
        }
    }

    public Task<bool> DeleteAsync(
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(logicalName);

        try
        {
            if (!File.Exists(path))
            {
                return Task.FromResult(false);
            }

            File.Delete(path);
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            throw new SecretStoreException(SecretStoreFailureCode.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            throw new SecretStoreException(SecretStoreFailureCode.StorageUnavailable);
        }
    }

    public bool Exists(string logicalName)
    {
        EnsureWindows();
        return File.Exists(GetPath(logicalName));
    }

    private string GetPath(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        if (logicalName.Length > 128 || logicalName.Any(char.IsControl))
        {
            throw new ArgumentException("The secret identifier is invalid.", nameof(logicalName));
        }

        var nameBytes = StrictUtf8.GetBytes(logicalName);
        try
        {
            var digest = SHA256.HashData(nameBytes);
            return Path.Combine(
                _directory,
                $"{Convert.ToHexString(digest).ToLowerInvariant()}.bin");
        }
        finally
        {
            Zero(nameBytes);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Current-user DPAPI storage requires Windows.");
        }
    }

    private static void Zero(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
