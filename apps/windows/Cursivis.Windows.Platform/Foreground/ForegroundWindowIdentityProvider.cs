using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cursivis.Windows.Platform.Foreground;

public sealed record ForegroundWindowIdentity(
    uint ProcessId,
    string ProcessName,
    string WindowTitle,
    nint WindowHandle = default);

public interface IForegroundWindowIdentityProvider
{
    ForegroundWindowIdentity? GetCurrent();
}

public sealed class Win32ForegroundWindowIdentityProvider : IForegroundWindowIdentityProvider
{
    private const int MaximumWindowTitleCharacters = 512;

    public ForegroundWindowIdentity? GetCurrent()
    {
        EnsureWindows();
        var window = GetForegroundWindow();
        if (window == nint.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var processName = string.Empty;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // The process can exit between foreground-window and process lookup.
        }
        catch (InvalidOperationException)
        {
            // The process can exit between foreground-window and process lookup.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Protected processes may deny metadata access.
        }

        var title = ReadWindowTitle(window);
        return new ForegroundWindowIdentity(processId, processName, title, window);
    }

    private static string ReadWindowTitle(nint window)
    {
        var reportedLength = GetWindowTextLength(window);
        var capacity = Math.Clamp(reportedLength + 1, 2, MaximumWindowTitleCharacters + 1);
        var buffer = new StringBuilder(capacity);
        var copied = GetWindowText(window, buffer, capacity);
        return copied <= 0 ? string.Empty : buffer.ToString(0, copied);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Foreground-window identity requires Windows.");
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(
        nint windowHandle,
        StringBuilder text,
        int maximumCharacters);
}
