using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Cursivis.Application.Context;
using Cursivis.Domain.Context;

namespace Cursivis.Windows.Platform.Selection;

public sealed class WindowsProtectedClipboardSelectionReader : IForegroundBoundTextSelectionReader
{
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyC = 0x43;
    private const uint UnicodeTextClipboardFormat = 13;
    private const uint AnsiTextClipboardFormat = 1;
    private static readonly TimeSpan ClipboardTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ClipboardPollInterval = TimeSpan.FromMilliseconds(8);
    private readonly IWindowsKeyboardInputSender _keyboardInput;

    public WindowsProtectedClipboardSelectionReader()
        : this(new WindowsKeyboardInputSender())
    {
    }

    internal WindowsProtectedClipboardSelectionReader(IWindowsKeyboardInputSender keyboardInput)
    {
        _keyboardInput = keyboardInput ?? throw new ArgumentNullException(nameof(keyboardInput));
    }

    public async Task<TextSelectionReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
        => await ReadForWindowAsync(nint.Zero, cancellationToken).ConfigureAwait(false);

    public async Task<TextSelectionReadResult> ReadForWindowAsync(
        nint sourceWindowHandle,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        if (sourceWindowHandle != nint.Zero && GetForegroundWindow() != sourceWindowHandle)
        {
            return Failed(
                TextSelectionReadStatus.Unavailable,
                "The source application changed before the selection could be copied.");
        }

        uint baselineSequence = GetClipboardSequenceNumber();
        IDataObject? original = CaptureOriginalClipboard();
        uint copiedSequence = 0;
        try
        {
            // The foreground check immediately precedes SendInput. It prevents
            // Ctrl+C from being delivered to a new foreground app if a user
            // changes windows during the short UIA-to-clipboard fallback.
            if (sourceWindowHandle != nint.Zero && GetForegroundWindow() != sourceWindowHandle)
            {
                return Failed(
                    TextSelectionReadStatus.Unavailable,
                    "The source application changed before the selection could be copied.");
            }

            if (!SendCopyChord())
            {
                return Failed(TextSelectionReadStatus.Unavailable, "Windows could not send the copy command.");
            }

            long waitStarted = Stopwatch.GetTimestamp();
            while (GetClipboardSequenceNumber() == baselineSequence)
            {
                if (Stopwatch.GetElapsedTime(waitStarted) >= ClipboardTimeout)
                {
                    return Failed(TextSelectionReadStatus.NoSelection, "The foreground application did not publish a clipboard selection.");
                }

                try
                {
                    await Task.Delay(ClipboardPollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Failed(TextSelectionReadStatus.Cancelled, "Clipboard selection detection was cancelled.");
                }
            }

            copiedSequence = GetClipboardSequenceNumber();
            string? text = await ReadClipboardTextAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text)
                ? Failed(TextSelectionReadStatus.NoSelection, "The copied selection did not contain readable text.")
                : TextSelectionReadResult.Captured(text, ContextSource.ProtectedClipboard);
        }
        catch (COMException)
        {
            return Failed(TextSelectionReadStatus.Unavailable, "The Windows clipboard was unavailable.");
        }
        finally
        {
            RestoreOriginalClipboard(original, copiedSequence);
        }
    }

    private static async Task<string?> ReadClipboardTextAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OpenClipboard(nint.Zero))
            {
                try
                {
                    string? unicode = ReadClipboardString(UnicodeTextClipboardFormat, unicode: true);
                    if (!string.IsNullOrWhiteSpace(unicode))
                    {
                        return unicode;
                    }

                    return ReadClipboardString(AnsiTextClipboardFormat, unicode: false);
                }
                finally
                {
                    _ = CloseClipboard();
                }
            }

            await Task.Delay(ClipboardPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static string? ReadClipboardString(uint format, bool unicode)
    {
        if (!IsClipboardFormatAvailable(format))
        {
            return null;
        }

        nint memory = GetClipboardData(format);
        if (memory == nint.Zero)
        {
            return null;
        }

        nint pointer = GlobalLock(memory);
        if (pointer == nint.Zero)
        {
            return null;
        }

        try
        {
            nuint byteCount = GlobalSize(memory);
            int maximumCharacters = ContextTriggerService.MaximumSelectionCharacters + 1;
            int availableCharacters = unicode
                ? checked((int)Math.Min(byteCount / 2, (nuint)maximumCharacters))
                : checked((int)Math.Min(byteCount, (nuint)maximumCharacters));
            if (availableCharacters <= 0)
            {
                return null;
            }

            string? value = unicode
                ? Marshal.PtrToStringUni(pointer, availableCharacters)
                : Marshal.PtrToStringAnsi(pointer, availableCharacters);
            return value?.TrimEnd('\0');
        }
        finally
        {
            _ = GlobalUnlock(memory);
        }
    }

    internal static IDataObject? CaptureOriginalClipboard()
    {
        int result = OleGetClipboard(out IDataObject? original);
        return result >= 0 ? original : null;
    }

    internal static void RestoreOriginalClipboard(IDataObject? original, uint copiedSequence)
    {
        try
        {
            if (copiedSequence != 0 && GetClipboardSequenceNumber() == copiedSequence)
            {
                _ = OleSetClipboard(original);
            }
        }
        finally
        {
            if (original is not null && Marshal.IsComObject(original))
            {
                _ = Marshal.FinalReleaseComObject(original);
            }
        }
    }

    private bool SendCopyChord() =>
        _keyboardInput.TrySendChord(VirtualKeyControl, VirtualKeyC);

    private static TextSelectionReadResult Failed(TextSelectionReadStatus status, string detail) =>
        TextSelectionReadResult.Failed(status, ContextSource.ProtectedClipboard, detail);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected clipboard selection requires Windows.");
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint ownerWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint memory);

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out IDataObject? dataObject);

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] IDataObject? dataObject);
}
