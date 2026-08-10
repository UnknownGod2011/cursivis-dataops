using System.Runtime.InteropServices;

namespace Cursivis.Windows.Platform.Hotkeys;

public enum NativeHotkeyFailure
{
    None,
    Conflict,
    NotRegistered,
    OperatingSystemError,
}

public readonly record struct NativeHotkeyOperationResult(
    bool Succeeded,
    NativeHotkeyFailure Failure,
    int NativeErrorCode)
{
    public static NativeHotkeyOperationResult Success { get; } = new(true, NativeHotkeyFailure.None, 0);
}

public interface INativeHotkeyApi
{
    NativeHotkeyOperationResult Register(
        nint windowHandle,
        int registrationId,
        HotkeyChord chord);

    NativeHotkeyOperationResult RegisterUnmodified(
        nint windowHandle,
        int registrationId,
        uint virtualKey);

    NativeHotkeyOperationResult Unregister(
        nint windowHandle,
        int registrationId);
}

public sealed class Win32NativeHotkeyApi : INativeHotkeyApi
{
    private const uint NoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const int ErrorHotkeyNotRegistered = 1419;

    public NativeHotkeyOperationResult Register(
        nint windowHandle,
        int registrationId,
        HotkeyChord chord)
    {
        return RegisterCore(
            windowHandle,
            registrationId,
            (uint)chord.Modifiers,
            chord.VirtualKey);
    }

    public NativeHotkeyOperationResult RegisterUnmodified(
        nint windowHandle,
        int registrationId,
        uint virtualKey)
    {
        if (virtualKey is 0 or > 0xFF)
        {
            throw new ArgumentOutOfRangeException(nameof(virtualKey));
        }

        return RegisterCore(windowHandle, registrationId, 0, virtualKey);
    }

    private static NativeHotkeyOperationResult RegisterCore(
        nint windowHandle,
        int registrationId,
        uint modifiers,
        uint virtualKey)
    {
        EnsureWindows();
        if (RegisterHotKey(
                windowHandle,
                registrationId,
                modifiers | NoRepeat,
                virtualKey))
        {
            return NativeHotkeyOperationResult.Success;
        }

        var error = Marshal.GetLastWin32Error();
        return new NativeHotkeyOperationResult(
            false,
            error == ErrorHotkeyAlreadyRegistered
                ? NativeHotkeyFailure.Conflict
                : NativeHotkeyFailure.OperatingSystemError,
            error);
    }

    public NativeHotkeyOperationResult Unregister(
        nint windowHandle,
        int registrationId)
    {
        EnsureWindows();
        if (UnregisterHotKey(windowHandle, registrationId))
        {
            return NativeHotkeyOperationResult.Success;
        }

        var error = Marshal.GetLastWin32Error();
        return new NativeHotkeyOperationResult(
            false,
            error == ErrorHotkeyNotRegistered
                ? NativeHotkeyFailure.NotRegistered
                : NativeHotkeyFailure.OperatingSystemError,
            error);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Global hotkeys require Windows.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint windowHandle,
        int registrationId,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int registrationId);
}
