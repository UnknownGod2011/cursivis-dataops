using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cursivis.Contracts.Browser;

namespace Cursivis.Infrastructure.BrowserBridge;

public sealed record AuthenticatedBrowserSession(
    string SessionId,
    string ExtensionId,
    DateTimeOffset ExpiresAt,
    IReadOnlySet<string> Capabilities);

public sealed record BrowserAuthenticationResult(
    bool Succeeded,
    AuthenticatedBrowserSession? Session,
    BrowserError? Error)
{
    public static BrowserAuthenticationResult Reject(string code, string message, bool retryable = false) =>
        new(false, null, new BrowserError(code, message, retryable));

    public static BrowserAuthenticationResult Accept(AuthenticatedBrowserSession session) =>
        new(true, session, null);
}

public sealed class BrowserSessionRegistry(TimeProvider? timeProvider = null)
{
    private const int MaximumRequestsPerWindow = 30;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public void Register(string extensionId, BrowserWelcome welcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentNullException.ThrowIfNull(welcome);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (welcome.ExpiresAt <= now || welcome.ExpiresAt - now > BrowserBridgeLimits.SessionLifetime + TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException("The browser session expiry is invalid.", nameof(welcome));
        }

        string key = HashToken(welcome.SessionToken);
        SessionState state = new(
            new AuthenticatedBrowserSession(
                welcome.SessionId,
                extensionId,
                welcome.ExpiresAt,
                welcome.GrantedCapabilities.ToHashSet(StringComparer.Ordinal)),
            key);

        if (!_sessions.TryAdd(key, state))
        {
            throw new InvalidOperationException("The generated browser session token collided with an active session.");
        }

        RemoveExpired(now);
    }

    public BrowserAuthenticationResult Authenticate(BrowserEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrWhiteSpace(envelope.SessionToken))
        {
            return BrowserAuthenticationResult.Reject("session_required", "The browser session is not authenticated.");
        }

        string key = HashToken(envelope.SessionToken);
        if (!_sessions.TryGetValue(key, out SessionState? state)
            || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(key),
                Convert.FromHexString(state.TokenHash)))
        {
            return BrowserAuthenticationResult.Reject("invalid_session", "The browser session is not authenticated.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (state.Session.ExpiresAt <= now)
        {
            _sessions.TryRemove(key, out _);
            return BrowserAuthenticationResult.Reject("expired_session", "The browser session has expired.", true);
        }

        lock (state.SyncRoot)
        {
            while (state.RequestTimes.TryPeek(out DateTimeOffset oldest) && now - oldest >= RateWindow)
            {
                state.RequestTimes.TryDequeue(out _);
            }

            if (state.RequestTimes.Count >= MaximumRequestsPerWindow)
            {
                return BrowserAuthenticationResult.Reject("rate_limited", "The browser bridge is receiving requests too quickly.", true);
            }

            if (!state.CorrelationIds.Add(envelope.CorrelationId))
            {
                return BrowserAuthenticationResult.Reject("replayed_message", "The browser bridge message has already been processed.");
            }

            state.RequestTimes.Enqueue(now);
            if (state.CorrelationIds.Count > 2_048)
            {
                state.CorrelationIds.Clear();
                state.CorrelationIds.Add(envelope.CorrelationId);
            }
        }

        return BrowserAuthenticationResult.Accept(state.Session);
    }

    public bool Revoke(string sessionId)
    {
        foreach ((string key, SessionState state) in _sessions)
        {
            if (string.Equals(state.Session.SessionId, sessionId, StringComparison.Ordinal))
            {
                return _sessions.TryRemove(key, out _);
            }
        }

        return false;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach ((string key, SessionState state) in _sessions)
        {
            if (state.Session.ExpiresAt <= now)
            {
                _sessions.TryRemove(key, out _);
            }
        }
    }

    private static string HashToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 40 or > 128)
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private sealed class SessionState(AuthenticatedBrowserSession session, string tokenHash)
    {
        public AuthenticatedBrowserSession Session { get; } = session;
        public string TokenHash { get; } = tokenHash;
        public object SyncRoot { get; } = new();
        public Queue<DateTimeOffset> RequestTimes { get; } = new();
        public HashSet<string> CorrelationIds { get; } = new(StringComparer.Ordinal);
    }
}
