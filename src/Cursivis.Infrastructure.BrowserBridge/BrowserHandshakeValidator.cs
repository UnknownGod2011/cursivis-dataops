using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Cursivis.Contracts.Browser;

namespace Cursivis.Infrastructure.BrowserBridge;

public sealed record BrowserHandshakeOptions(
    IReadOnlySet<string> AllowedExtensionIds,
    IReadOnlySet<string> SupportedCapabilities);

public sealed record BrowserHandshakeResult(
    bool Succeeded,
    BrowserWelcome? Welcome,
    BrowserError? Error)
{
    public static BrowserHandshakeResult Reject(string code, string message) =>
        new(false, null, new BrowserError(code, message, false));

    public static BrowserHandshakeResult Accept(BrowserWelcome welcome) =>
        new(true, welcome, null);
}

public sealed class BrowserHandshakeValidator
{
    private readonly BrowserHandshakeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenNonces = new(StringComparer.Ordinal);

    public BrowserHandshakeValidator(BrowserHandshakeOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowedExtensionIds.Count == 0)
        {
            throw new ArgumentException("At least one extension identifier must be allowed.", nameof(options));
        }

        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BrowserHandshakeResult Validate(BrowserEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, BrowserMessageTypes.Hello, StringComparison.Ordinal))
        {
            return BrowserHandshakeResult.Reject("handshake_required", "The first browser bridge message must be a handshake.");
        }

        BrowserHello? hello;
        try
        {
            hello = envelope.Payload.Deserialize<BrowserHello>(BrowserJson.SerializerOptions);
        }
        catch (JsonException)
        {
            return BrowserHandshakeResult.Reject("invalid_handshake", "The browser bridge handshake payload is invalid.");
        }

        if (hello is null
            || !_options.AllowedExtensionIds.Contains(hello.ExtensionId)
            || string.IsNullOrWhiteSpace(hello.ExtensionVersion))
        {
            return BrowserHandshakeResult.Reject("extension_not_allowed", "The browser extension is not registered for this installation.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (hello.ExpiresAt <= now || hello.ExpiresAt - now > BrowserBridgeLimits.MaximumHandshakeLifetime)
        {
            return BrowserHandshakeResult.Reject("expired_handshake", "The browser bridge handshake has expired.");
        }

        if (!IsValidNonce(hello.Nonce))
        {
            return BrowserHandshakeResult.Reject("invalid_nonce", "The browser bridge handshake nonce is invalid.");
        }

        RemoveExpiredNonces(now);
        if (!_seenNonces.TryAdd(hello.Nonce, hello.ExpiresAt))
        {
            return BrowserHandshakeResult.Reject("replayed_handshake", "The browser bridge handshake has already been used.");
        }

        string[] grantedCapabilities = hello.RequestedCapabilities
            .Where(_options.SupportedCapabilities.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        BrowserWelcome welcome = new(
            Guid.NewGuid().ToString("N"),
            CreateOpaqueToken(),
            now.Add(BrowserBridgeLimits.SessionLifetime),
            grantedCapabilities);
        return BrowserHandshakeResult.Accept(welcome);
    }

    private static bool IsValidNonce(string nonce)
    {
        if (nonce.Length is < 32 or > 128)
        {
            return false;
        }

        return nonce.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static string CreateOpaqueToken()
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return token.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void RemoveExpiredNonces(DateTimeOffset now)
    {
        foreach ((string nonce, DateTimeOffset expiry) in _seenNonces)
        {
            if (expiry <= now)
            {
                _seenNonces.TryRemove(nonce, out _);
            }
        }
    }
}
