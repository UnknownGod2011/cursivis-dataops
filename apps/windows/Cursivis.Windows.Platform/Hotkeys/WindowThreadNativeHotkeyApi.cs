namespace Cursivis.Windows.Platform.Hotkeys;

/// <summary>
/// Provides access to the thread that owns a window's native hotkey
/// registrations. Implementations must enqueue without running callbacks on an
/// arbitrary worker thread.
/// </summary>
public interface IWindowThreadDispatcher
{
    bool HasThreadAccess { get; }

    bool TryEnqueue(Action action);

    /// <summary>
    /// Runs asynchronous work on the owner UI thread and returns its result.
    /// Clipboard, UIA, and WinUI overlay construction require this thread.
    /// </summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Windows associates RegisterHotKey and UnregisterHotKey calls for an HWND
/// with its owning thread. This adapter keeps the transactional registrar safe
/// when persistence continuations run on a worker thread.
/// </summary>
public sealed class WindowThreadNativeHotkeyApi : INativeHotkeyApi
{
    private readonly IWindowThreadDispatcher _dispatcher;
    private readonly INativeHotkeyApi _inner;

    public WindowThreadNativeHotkeyApi(
        IWindowThreadDispatcher dispatcher,
        INativeHotkeyApi inner)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public NativeHotkeyOperationResult Register(
        nint windowHandle,
        int registrationId,
        HotkeyChord chord) =>
        Invoke(() => _inner.Register(windowHandle, registrationId, chord));

    public NativeHotkeyOperationResult RegisterUnmodified(
        nint windowHandle,
        int registrationId,
        uint virtualKey) =>
        Invoke(() => _inner.RegisterUnmodified(windowHandle, registrationId, virtualKey));

    public NativeHotkeyOperationResult Unregister(
        nint windowHandle,
        int registrationId) =>
        Invoke(() => _inner.Unregister(windowHandle, registrationId));

    private T Invoke<T>(Func<T> operation)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return operation();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    completion.TrySetResult(operation());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            throw new InvalidOperationException("The Cursivis Settings dispatcher is unavailable for hotkey registration.");
        }

        return completion.Task.GetAwaiter().GetResult();
    }
}
