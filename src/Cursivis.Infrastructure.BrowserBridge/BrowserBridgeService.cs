using System.Text.Json;
using Cursivis.Application.Actions;
using Cursivis.Contracts.Browser;

namespace Cursivis.Infrastructure.BrowserBridge;

public enum BrowserBridgeConnectionState
{
    Stopped,
    Waiting,
    Connected,
    Error,
}

public sealed record BrowserBridgeSnapshot(
    BrowserBridgeConnectionState State,
    string SafeStatus,
    string? ExtensionVersion,
    DateTimeOffset? LastAuthenticatedHandshake,
    IReadOnlyList<string> Capabilities)
{
    public bool IsConnected => State == BrowserBridgeConnectionState.Connected;

    public static BrowserBridgeSnapshot Stopped { get; } = new(
        BrowserBridgeConnectionState.Stopped,
        "Browser integration is stopped.",
        null,
        null,
        []);
}

public sealed class BrowserBridgeException : InvalidOperationException
{
    public BrowserBridgeException(string code, string safeMessage, bool retryable = false, Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}

/// <summary>
/// Owns the same-user named-pipe endpoint used by the Chromium native host. The
/// browser is untrusted until a versioned, nonce-bound handshake succeeds. All
/// requests are correlated, deadline bounded, and deserialized with the strict
/// browser contract options before they reach application code.
/// </summary>
public sealed class BrowserBridgeService : IBrowserActionGateway, IAsyncDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(7);
    private static readonly IReadOnlySet<string> SupportedCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "selection",
        "form_discovery",
        "safe_form_fill",
    };

    private readonly BrowserPipeServer _pipeServer;
    private readonly BrowserEnvelopeValidator _envelopeValidator;
    private readonly BrowserHandshakeValidator _handshakeValidator;
    private readonly BrowserSessionRegistry _sessionRegistry;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _connectionGate = new();
    private Connection? _connection;
    private Task? _acceptLoop;
    private BrowserBridgeSnapshot _snapshot = BrowserBridgeSnapshot.Stopped;
    private bool _disposed;

    public BrowserBridgeService(
        IReadOnlySet<string> allowedExtensionIds,
        string? pipeName = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(allowedExtensionIds);
        if (allowedExtensionIds.Count == 0)
        {
            throw new ArgumentException("At least one extension identifier must be allowed.", nameof(allowedExtensionIds));
        }

        TimeProvider clock = timeProvider ?? TimeProvider.System;
        _pipeServer = new BrowserPipeServer(pipeName ?? BrowserPipeNames.ForCurrentUser());
        _envelopeValidator = new BrowserEnvelopeValidator(clock);
        _handshakeValidator = new BrowserHandshakeValidator(
            new BrowserHandshakeOptions(allowedExtensionIds, SupportedCapabilities),
            clock);
        _sessionRegistry = new BrowserSessionRegistry(clock);
    }

    public event Action<BrowserBridgeSnapshot>? SnapshotChanged;

    public BrowserBridgeSnapshot Snapshot
    {
        get
        {
            lock (_connectionGate)
            {
                return _snapshot;
            }
        }
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_connectionGate)
        {
            _acceptLoop ??= AcceptLoopAsync(_lifetime.Token);
        }

        Publish(new(
            BrowserBridgeConnectionState.Waiting,
            "Waiting for the Cursivis browser extension.",
            null,
            null,
            []));
        return Task.CompletedTask;
    }

    public Task<BrowserSelectionResponse> GetSelectionAsync(
        bool includeNearbySemanticContext = true,
        int maximumCharacters = BrowserBridgeLimits.MaximumSelectionCharacters,
        CancellationToken cancellationToken = default)
    {
        if (maximumCharacters is < 1 or > BrowserBridgeLimits.MaximumSelectionCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        return SendRequestAsync<BrowserSelectionResponse>(
            BrowserMessageTypes.GetSelection,
            new
            {
                includeNearbySemanticContext,
                maximumCharacters,
            },
            BrowserMessageTypes.Selection,
            DefaultRequestTimeout,
            cancellationToken);
    }

    public Task<BrowserFormSnapshot> DiscoverFormAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<BrowserFormSnapshot>(
            BrowserMessageTypes.DiscoverForm,
            new { },
            BrowserMessageTypes.Form,
            DefaultRequestTimeout,
            cancellationToken);

    public Task<BrowserStepResult> ExecuteAsync(
        BrowserCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SendRequestAsync<BrowserStepResult>(
            BrowserMessageTypes.Execute,
            command,
            BrowserMessageTypes.StepResult,
            DefaultRequestTimeout,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        Connection? connection;
        Task? acceptLoop;
        lock (_connectionGate)
        {
            connection = _connection;
            _connection = null;
            acceptLoop = _acceptLoop;
        }

        connection?.Dispose(new BrowserBridgeException(
            "bridge_stopped",
            "Browser integration stopped."));
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        _lifetime.Dispose();
        Publish(BrowserBridgeSnapshot.Stopped);
    }

    private async Task<TResponse> SendRequestAsync<TResponse>(
        string requestType,
        object payload,
        string expectedResponseType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Connection connection;
        lock (_connectionGate)
        {
            connection = _connection ?? throw new BrowserBridgeException(
                "bridge_disconnected",
                "The Cursivis browser extension is not connected.",
                retryable: true);
        }

        string correlationId = Guid.NewGuid().ToString("N");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        deadline.CancelAfter(timeout);
        BrowserEnvelope response;
        try
        {
            response = await connection.SendAsync(
                BrowserEnvelope.Create(
                    requestType,
                    correlationId,
                    payload,
                    sessionToken: connection.Welcome.SessionToken),
                correlationId,
                deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new BrowserBridgeException(
                "browser_request_timeout",
                "The browser did not respond in time.",
                retryable: true);
        }

        if (string.Equals(response.Type, BrowserMessageTypes.Error, StringComparison.Ordinal))
        {
            BrowserError error = DeserializePayload<BrowserError>(response);
            throw new BrowserBridgeException(error.Code, error.Message, error.Retryable);
        }

        if (!string.Equals(response.Type, expectedResponseType, StringComparison.Ordinal))
        {
            throw new BrowserBridgeException(
                "unexpected_browser_response",
                "The browser returned an unexpected response.");
        }

        return DeserializePayload<TResponse>(response);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = _pipeServer.CreateServer();
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ProcessConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or JsonException or BrowserBridgeException)
            {
                Publish(new(
                    BrowserBridgeConnectionState.Error,
                    "The browser connection closed unexpectedly.",
                    null,
                    null,
                    []));
            }
            finally
            {
                ClearConnection();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                Publish(new(
                    BrowserBridgeConnectionState.Waiting,
                    "Waiting for the Cursivis browser extension.",
                    null,
                    null,
                    []));
            }
        }
    }

    private async Task ProcessConnectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        BrowserEnvelope first = await NativeMessagingFraming.ReadEnvelopeAsync(pipe, cancellationToken).ConfigureAwait(false)
            ?? throw new BrowserBridgeException("handshake_required", "The browser connection closed before authentication.");
        BrowserValidationResult envelopeValidation = _envelopeValidator.Validate(first);
        if (!envelopeValidation.IsValid)
        {
            await WriteErrorAsync(pipe, first.CorrelationId, envelopeValidation, cancellationToken).ConfigureAwait(false);
            return;
        }

        BrowserHandshakeResult handshake = _handshakeValidator.Validate(first);
        if (!handshake.Succeeded || handshake.Welcome is null)
        {
            BrowserError error = handshake.Error ?? new BrowserError(
                "invalid_handshake",
                "The browser handshake was rejected.",
                false);
            await NativeMessagingFraming.WriteEnvelopeAsync(
                pipe,
                BrowserEnvelope.Create(BrowserMessageTypes.Error, first.CorrelationId, error),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        BrowserHello hello = DeserializePayload<BrowserHello>(first);
        BrowserWelcome welcome = handshake.Welcome;
        _sessionRegistry.Register(hello.ExtensionId, welcome);
        var connection = new Connection(pipe, welcome);
        lock (_connectionGate)
        {
            _connection?.Dispose(new BrowserBridgeException(
                "bridge_replaced",
                "A newer authenticated browser connection replaced this one.",
                retryable: true));
            _connection = connection;
        }

        Publish(new(
            BrowserBridgeConnectionState.Connected,
            "Authenticated browser connection ready.",
            hello.ExtensionVersion,
            DateTimeOffset.UtcNow,
            welcome.GrantedCapabilities));

        // Publish the authenticated connection before sending Welcome. Once the
        // extension observes Welcome it may issue a request immediately, and the
        // bridge must already be ready to accept it without a visibility race.
        await NativeMessagingFraming.WriteEnvelopeAsync(
            pipe,
            BrowserEnvelope.Create(
                BrowserMessageTypes.Welcome,
                first.CorrelationId,
                welcome,
                sessionToken: welcome.SessionToken),
            cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            BrowserEnvelope? envelope = await NativeMessagingFraming.ReadEnvelopeAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                break;
            }

            BrowserValidationResult validation = _envelopeValidator.Validate(envelope);
            if (!validation.IsValid)
            {
                connection.Fail(envelope.CorrelationId, new BrowserBridgeException(
                    validation.ErrorCode ?? "invalid_browser_message",
                    validation.SafeMessage ?? "The browser returned an invalid message."));
                continue;
            }

            BrowserAuthenticationResult authentication = _sessionRegistry.Authenticate(envelope);
            if (!authentication.Succeeded)
            {
                BrowserError error = authentication.Error!;
                connection.Fail(envelope.CorrelationId, new BrowserBridgeException(
                    error.Code,
                    error.Message,
                    error.Retryable));
                continue;
            }

            connection.Complete(envelope);
        }

        connection.Dispose(new BrowserBridgeException(
            "bridge_disconnected",
            "The Cursivis browser extension disconnected.",
            retryable: true));
    }

    private void ClearConnection()
    {
        lock (_connectionGate)
        {
            _connection?.Dispose(new BrowserBridgeException(
                "bridge_disconnected",
                "The Cursivis browser extension disconnected.",
                retryable: true));
            _connection = null;
        }
    }

    private void Publish(BrowserBridgeSnapshot snapshot)
    {
        lock (_connectionGate)
        {
            _snapshot = snapshot;
        }

        SnapshotChanged?.Invoke(snapshot);
    }

    private static T DeserializePayload<T>(BrowserEnvelope envelope)
    {
        try
        {
            return envelope.Payload.Deserialize<T>(BrowserJson.SerializerOptions)
                ?? throw new JsonException("The browser payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new BrowserBridgeException(
                "invalid_browser_payload",
                "The browser returned data that did not match the required structure.",
                innerException: exception);
        }
    }

    private static ValueTask WriteErrorAsync(
        Stream pipe,
        string correlationId,
        BrowserValidationResult validation,
        CancellationToken cancellationToken) =>
        NativeMessagingFraming.WriteEnvelopeAsync(
            pipe,
            BrowserEnvelope.Create(
                BrowserMessageTypes.Error,
                correlationId,
                new BrowserError(
                    validation.ErrorCode ?? "invalid_browser_message",
                    validation.SafeMessage ?? "The browser message was rejected.",
                    false)),
            cancellationToken);

    private sealed class Connection(Stream stream, BrowserWelcome welcome)
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Dictionary<string, TaskCompletionSource<BrowserEnvelope>> _pending = new(StringComparer.Ordinal);
        private readonly object _pendingGate = new();
        private bool _disposed;

        public BrowserWelcome Welcome { get; } = welcome;

        public async Task<BrowserEnvelope> SendAsync(
            BrowserEnvelope envelope,
            string correlationId,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<BrowserEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_pending.TryAdd(correlationId, completion))
                {
                    throw new InvalidOperationException("The browser correlation identifier is already pending.");
                }
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<BrowserEnvelope>)state!).TrySetCanceled(),
                completion);
            try
            {
                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await NativeMessagingFraming.WriteEnvelopeAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }

                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_pendingGate)
                {
                    _pending.Remove(correlationId);
                }
            }
        }

        public void Complete(BrowserEnvelope envelope)
        {
            TaskCompletionSource<BrowserEnvelope>? completion;
            lock (_pendingGate)
            {
                _pending.TryGetValue(envelope.CorrelationId, out completion);
            }

            completion?.TrySetResult(envelope);
        }

        public void Fail(string correlationId, Exception exception)
        {
            TaskCompletionSource<BrowserEnvelope>? completion;
            lock (_pendingGate)
            {
                _pending.TryGetValue(correlationId, out completion);
            }

            completion?.TrySetException(exception);
        }

        public void Dispose(Exception reason)
        {
            TaskCompletionSource<BrowserEnvelope>[] pending;
            lock (_pendingGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                pending = _pending.Values.ToArray();
                _pending.Clear();
            }

            foreach (TaskCompletionSource<BrowserEnvelope> completion in pending)
            {
                completion.TrySetException(reason);
            }
        }
    }
}
