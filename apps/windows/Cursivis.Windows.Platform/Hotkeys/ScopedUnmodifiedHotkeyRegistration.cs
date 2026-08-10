namespace Cursivis.Windows.Platform.Hotkeys;

/// <summary>
/// Owns an unmodified global key only for the lifetime of a transient surface.
/// The registration ID must remain outside the persisted registrar's private range.
/// </summary>
public sealed class ScopedUnmodifiedHotkeyRegistration : IDisposable
{
    private readonly nint _windowHandle;
    private readonly int _registrationId;
    private readonly uint _virtualKey;
    private readonly INativeHotkeyApi _nativeApi;
    private bool _disposed;

    public ScopedUnmodifiedHotkeyRegistration(
        nint windowHandle,
        int registrationId,
        uint virtualKey,
        INativeHotkeyApi nativeApi)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A message-window handle is required.", nameof(windowHandle));
        }

        if (registrationId is <= 0 or >= 0x4000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(registrationId),
                "Transient hotkey IDs must remain below the persisted registrar range.");
        }

        if (virtualKey is 0 or > 0xFF)
        {
            throw new ArgumentOutOfRangeException(nameof(virtualKey));
        }

        _windowHandle = windowHandle;
        _registrationId = registrationId;
        _virtualKey = virtualKey;
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
    }

    public int RegistrationId => _registrationId;

    public bool IsEnabled { get; private set; }

    public bool Enable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsEnabled)
        {
            return true;
        }

        NativeHotkeyOperationResult result = _nativeApi.RegisterUnmodified(
            _windowHandle,
            _registrationId,
            _virtualKey);
        IsEnabled = result.Succeeded;
        return IsEnabled;
    }

    public void Disable()
    {
        if (_disposed || !IsEnabled)
        {
            return;
        }

        NativeHotkeyOperationResult result = _nativeApi.Unregister(
            _windowHandle,
            _registrationId);
        if (result.Succeeded || result.Failure == NativeHotkeyFailure.NotRegistered)
        {
            IsEnabled = false;
        }
    }

    public bool Matches(int registrationId) =>
        IsEnabled && registrationId == _registrationId;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Disable();
        _disposed = true;
    }
}
