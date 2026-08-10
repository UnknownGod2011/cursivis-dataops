namespace Cursivis.Windows.Platform.Hotkeys;

public interface IHotkeyStatePersister
{
    Task PersistAsync(
        string commandName,
        HotkeyChord chord,
        CancellationToken cancellationToken = default);
}

public sealed record ActiveHotkeyRegistration(
    string CommandName,
    int RegistrationId,
    HotkeyChord Chord);

public enum HotkeyUpdateStatus
{
    Success,
    AlreadyActive,
    NotRegistered,
    Conflict,
    NativeRegistrationFailed,
    PersistenceFailed,
    NativeReleaseFailed,
    RollbackFailed,
}

public sealed record HotkeyUpdateResult(
    HotkeyUpdateStatus Status,
    HotkeyChord? ActiveChord,
    HotkeyChord ProposedChord,
    int NativeErrorCode,
    bool RollbackCompleted);

public sealed class TransactionalHotkeyRegistrar : IAsyncDisposable
{
    private const int FirstRegistrationId = 0x4000;
    private const int LastRegistrationId = 0xBFFF;

    private readonly nint _windowHandle;
    private readonly INativeHotkeyApi _nativeApi;
    private readonly IHotkeyStatePersister _persister;
    private readonly Dictionary<string, ActiveHotkeyRegistration> _active =
        new(StringComparer.Ordinal);
    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextRegistrationId = FirstRegistrationId;
    private bool _disposed;

    public TransactionalHotkeyRegistrar(
        nint windowHandle,
        INativeHotkeyApi nativeApi,
        IHotkeyStatePersister persister)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException(
                "A message-window handle is required for deterministic hotkey ownership.",
                nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        _persister = persister ?? throw new ArgumentNullException(nameof(persister));
    }

    public async Task<HotkeyUpdateResult> RegisterInitialAsync(
        string commandName,
        HotkeyChord chord,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandName(commandName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ActiveHotkeyRegistration? existing;
            lock (_stateSync)
            {
                _active.TryGetValue(commandName, out existing);
            }

            if (existing is not null)
            {
                return new HotkeyUpdateResult(
                    HotkeyUpdateStatus.AlreadyActive,
                    existing.Chord,
                    chord,
                    0,
                    RollbackCompleted: true);
            }

            var registrationId = AllocateRegistrationId();
            var nativeResult = _nativeApi.Register(_windowHandle, registrationId, chord);
            if (!nativeResult.Succeeded)
            {
                return new HotkeyUpdateResult(
                    nativeResult.Failure == NativeHotkeyFailure.Conflict
                        ? HotkeyUpdateStatus.Conflict
                        : HotkeyUpdateStatus.NativeRegistrationFailed,
                    null,
                    chord,
                    nativeResult.NativeErrorCode,
                    RollbackCompleted: true);
            }

            lock (_stateSync)
            {
                _active.Add(
                    commandName,
                    new ActiveHotkeyRegistration(commandName, registrationId, chord));
            }
            return new HotkeyUpdateResult(
                HotkeyUpdateStatus.Success,
                chord,
                chord,
                0,
                RollbackCompleted: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HotkeyUpdateResult> UpdateAsync(
        string commandName,
        HotkeyChord proposedChord,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandName(commandName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ActiveHotkeyRegistration? previous;
            lock (_stateSync)
            {
                _active.TryGetValue(commandName, out previous);
            }

            if (previous is null)
            {
                // A command can be inactive after a startup conflict. Settings
                // must be able to repair that command without requiring an app
                // restart, while retaining the same register-then-persist
                // transaction used for an ordinary replacement.
                var initialRegistrationId = AllocateRegistrationId();
                var initialRegistration = _nativeApi.Register(
                    _windowHandle,
                    initialRegistrationId,
                    proposedChord);
                if (!initialRegistration.Succeeded)
                {
                    return new HotkeyUpdateResult(
                        initialRegistration.Failure == NativeHotkeyFailure.Conflict
                            ? HotkeyUpdateStatus.Conflict
                            : HotkeyUpdateStatus.NativeRegistrationFailed,
                        null,
                        proposedChord,
                        initialRegistration.NativeErrorCode,
                        RollbackCompleted: true);
                }

                try
                {
                    await _persister.PersistAsync(
                        commandName,
                        proposedChord,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    var cancellationRollback = _nativeApi.Unregister(
                        _windowHandle,
                        initialRegistrationId);
                    if (cancellationRollback.Succeeded)
                    {
                        throw;
                    }

                    return new HotkeyUpdateResult(
                        HotkeyUpdateStatus.RollbackFailed,
                        null,
                        proposedChord,
                        cancellationRollback.NativeErrorCode,
                        RollbackCompleted: false);
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    var releaseProposed = _nativeApi.Unregister(
                        _windowHandle,
                        initialRegistrationId);
                    return new HotkeyUpdateResult(
                        releaseProposed.Succeeded
                            ? HotkeyUpdateStatus.PersistenceFailed
                            : HotkeyUpdateStatus.RollbackFailed,
                        null,
                        proposedChord,
                        releaseProposed.NativeErrorCode,
                        RollbackCompleted: releaseProposed.Succeeded);
                }

                lock (_stateSync)
                {
                    _active.Add(
                        commandName,
                        new ActiveHotkeyRegistration(commandName, initialRegistrationId, proposedChord));
                }

                return new HotkeyUpdateResult(
                    HotkeyUpdateStatus.Success,
                    proposedChord,
                    proposedChord,
                    0,
                    RollbackCompleted: true);
            }

            if (previous.Chord == proposedChord)
            {
                return new HotkeyUpdateResult(
                    HotkeyUpdateStatus.AlreadyActive,
                    previous.Chord,
                    proposedChord,
                    0,
                    RollbackCompleted: true);
            }

            var proposedRegistrationId = AllocateRegistrationId();
            var registrationResult = _nativeApi.Register(
                _windowHandle,
                proposedRegistrationId,
                proposedChord);
            if (!registrationResult.Succeeded)
            {
                return new HotkeyUpdateResult(
                    registrationResult.Failure == NativeHotkeyFailure.Conflict
                        ? HotkeyUpdateStatus.Conflict
                        : HotkeyUpdateStatus.NativeRegistrationFailed,
                    previous.Chord,
                    proposedChord,
                    registrationResult.NativeErrorCode,
                    RollbackCompleted: true);
            }

            try
            {
                await _persister.PersistAsync(
                    commandName,
                    proposedChord,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var cancellationRollback = _nativeApi.Unregister(
                    _windowHandle,
                    proposedRegistrationId);
                if (cancellationRollback.Succeeded)
                {
                    throw;
                }

                return new HotkeyUpdateResult(
                    HotkeyUpdateStatus.RollbackFailed,
                    previous.Chord,
                    proposedChord,
                    cancellationRollback.NativeErrorCode,
                    RollbackCompleted: false);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                var releaseProposed = _nativeApi.Unregister(
                    _windowHandle,
                    proposedRegistrationId);
                return new HotkeyUpdateResult(
                    releaseProposed.Succeeded
                        ? HotkeyUpdateStatus.PersistenceFailed
                        : HotkeyUpdateStatus.RollbackFailed,
                    previous.Chord,
                    proposedChord,
                    releaseProposed.NativeErrorCode,
                    RollbackCompleted: releaseProposed.Succeeded);
            }

            var releasePrevious = _nativeApi.Unregister(
                _windowHandle,
                previous.RegistrationId);
            if (!releasePrevious.Succeeded)
            {
                var releaseProposed = _nativeApi.Unregister(
                    _windowHandle,
                    proposedRegistrationId);
                var persistenceRestored = true;
                try
                {
                    await _persister.PersistAsync(
                        commandName,
                        previous.Chord,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    persistenceRestored = false;
                }

                var rollbackCompleted = releaseProposed.Succeeded && persistenceRestored;
                return new HotkeyUpdateResult(
                    rollbackCompleted
                        ? HotkeyUpdateStatus.NativeReleaseFailed
                        : HotkeyUpdateStatus.RollbackFailed,
                    previous.Chord,
                    proposedChord,
                    releasePrevious.NativeErrorCode,
                    rollbackCompleted);
            }

            var replacement = new ActiveHotkeyRegistration(
                commandName,
                proposedRegistrationId,
                proposedChord);
            lock (_stateSync)
            {
                _active[commandName] = replacement;
            }

            return new HotkeyUpdateResult(
                HotkeyUpdateStatus.Success,
                proposedChord,
                proposedChord,
                0,
                RollbackCompleted: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryGetActive(string commandName, out ActiveHotkeyRegistration? registration)
    {
        ValidateCommandName(commandName);
        lock (_stateSync)
        {
            if (_active.TryGetValue(commandName, out var value))
            {
                registration = value;
                return true;
            }

            registration = null;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            ActiveHotkeyRegistration[] registrations;
            lock (_stateSync)
            {
                registrations = [.. _active.Values];
                _active.Clear();
            }

            foreach (var registration in registrations)
            {
                _ = _nativeApi.Unregister(_windowHandle, registration.RegistrationId);
            }

            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private int AllocateRegistrationId()
    {
        var attempts = LastRegistrationId - FirstRegistrationId + 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var candidate = _nextRegistrationId++;
            if (_nextRegistrationId > LastRegistrationId)
            {
                _nextRegistrationId = FirstRegistrationId;
            }

            var candidateIsAvailable = false;
            lock (_stateSync)
            {
                candidateIsAvailable = _active.Values.All(
                    registration => registration.RegistrationId != candidate);
            }

            if (candidateIsAvailable)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No private hotkey registration IDs remain.");
    }

    private static void ValidateCommandName(string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        if (commandName.Length > 64 || commandName.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("The hotkey command name is invalid.", nameof(commandName));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
