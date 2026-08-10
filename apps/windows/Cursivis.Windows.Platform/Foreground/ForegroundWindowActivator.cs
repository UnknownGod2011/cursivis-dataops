using System.Runtime.InteropServices;

namespace Cursivis.Windows.Platform.Foreground;

public interface IForegroundWindowActivator
{
    bool TryActivate(nint windowHandle);
}

public sealed class Win32ForegroundWindowActivator : IForegroundWindowActivator
{
    private const int ShowRestore = 9;

    public bool TryActivate(nint windowHandle)
    {
        if (windowHandle == nint.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        if (IsIconic(windowHandle))
        {
            _ = ShowWindowAsync(windowHandle, ShowRestore);
        }

        return SetForegroundWindow(windowHandle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}
