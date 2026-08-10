using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Cursivis.Domain.Context;

public enum ContextKind
{
    Text,
    Email,
    Code,
    Question,
    Prose,
    Image,
    Form,
    UserInterface,
    Table,
    Other,
}

public enum ContextSource
{
    BrowserBridge,
    UserInterfaceAutomation,
    ProtectedClipboard,
    RegionCapture,
    DirectInput,
}

public sealed record TargetIdentity
{
    public TargetIdentity(
        string applicationId,
        string windowId,
        string? browserOrigin = null,
        long navigationGeneration = 0)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            throw new ArgumentException("An application identifier is required.", nameof(applicationId));
        }

        if (string.IsNullOrWhiteSpace(windowId))
        {
            throw new ArgumentException("A window identifier is required.", nameof(windowId));
        }

        if (navigationGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(navigationGeneration));
        }

        ApplicationId = applicationId.Trim();
        WindowId = windowId.Trim();
        BrowserOrigin = NormalizeOrigin(browserOrigin);
        NavigationGeneration = navigationGeneration;
    }

    public string ApplicationId { get; }

    public string WindowId { get; }

    public string? BrowserOrigin { get; }

    public long NavigationGeneration { get; }

    public override string ToString() =>
        $"TargetIdentity(Application={ApplicationId}, Window=<redacted>, BrowserOrigin={(BrowserOrigin is null ? "none" : "present")}, Generation={NavigationGeneration})";

    private static string? NormalizeOrigin(string? browserOrigin)
    {
        if (string.IsNullOrWhiteSpace(browserOrigin))
        {
            return null;
        }

        if (!Uri.TryCreate(browserOrigin.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Browser origin must be an absolute HTTP or HTTPS origin.", nameof(browserOrigin));
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant();
    }
}

public readonly record struct ContextFingerprint
{
    public const string Algorithm = "SHA-256";
    public const string Prefix = "sha256:";

    public ContextFingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(Prefix, StringComparison.Ordinal) ||
            value.Length != Prefix.Length + 64 ||
            !ContainsOnlyHexDigits(value.AsSpan(Prefix.Length)))
        {
            throw new ArgumentException("A valid SHA-256 context fingerprint is required.", nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static bool ContainsOnlyHexDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class ContextSnapshot
{
    public const int CurrentSchemaVersion = 1;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(15);

    private ContextSnapshot(
        ContextKind kind,
        ContextSource source,
        TargetIdentity target,
        string? text,
        ReadOnlyMemory<byte> imageDigest,
        DateTimeOffset capturedAt,
        DateTimeOffset expiresAt)
    {
        Kind = kind;
        Source = source;
        Target = target;
        Text = text;
        ImageDigest = imageDigest;
        CapturedAt = capturedAt;
        ExpiresAt = expiresAt;
        Fingerprint = ContextFingerprintFactory.Create(this);
    }

    public int SchemaVersion => CurrentSchemaVersion;

    public ContextKind Kind { get; }

    public ContextSource Source { get; }

    public TargetIdentity Target { get; }

    public string? Text { get; }

    public ReadOnlyMemory<byte> ImageDigest { get; }

    public DateTimeOffset CapturedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public ContextFingerprint Fingerprint { get; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public static ContextSnapshot FromText(
        ContextKind kind,
        ContextSource source,
        TargetIdentity target,
        string text,
        DateTimeOffset capturedAt,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (kind == ContextKind.Image)
        {
            throw new ArgumentException("Image context must be created from an image digest.", nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Captured text cannot be empty or whitespace.", nameof(text));
        }

        ValidateLifetime(lifetime);
        return new(kind, source, target, text, ReadOnlyMemory<byte>.Empty, capturedAt, capturedAt + lifetime);
    }

    public static ContextSnapshot FromImageDigest(
        ContextSource source,
        TargetIdentity target,
        ReadOnlySpan<byte> sha256Digest,
        DateTimeOffset capturedAt,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (sha256Digest.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("An image context requires a SHA-256 digest.", nameof(sha256Digest));
        }

        ValidateLifetime(lifetime);
        return new(
            ContextKind.Image,
            source,
            target,
            null,
            sha256Digest.ToArray(),
            capturedAt,
            capturedAt + lifetime);
    }

    public override string ToString() =>
        $"ContextSnapshot(Kind={Kind}, Source={Source}, Fingerprint={Fingerprint}, Text=<redacted>, Image=<redacted>, ExpiresAt={ExpiresAt:O})";

    private static void ValidateLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                $"Context lifetime must be greater than zero and no more than {MaximumLifetime.TotalMinutes} minutes.");
        }
    }
}

public static class ContextFingerprintFactory
{
    public static ContextFingerprint Create(ContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, snapshot.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, snapshot.Kind.ToString());
        Append(hash, snapshot.Source.ToString());
        Append(hash, snapshot.Target.ApplicationId);
        Append(hash, snapshot.Target.WindowId);
        Append(hash, snapshot.Target.BrowserOrigin ?? string.Empty);
        Append(hash, snapshot.Target.NavigationGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, snapshot.CapturedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        if (snapshot.Text is not null)
        {
            Append(hash, snapshot.Text);
        }
        else
        {
            Append(hash, snapshot.ImageDigest.Span);
        }

        var digest = hash.GetHashAndReset();
        return new ContextFingerprint($"{ContextFingerprint.Prefix}{Convert.ToHexString(digest).ToLowerInvariant()}");
    }

    private static void Append(IncrementalHash hash, string value) =>
        Append(hash, Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

}
