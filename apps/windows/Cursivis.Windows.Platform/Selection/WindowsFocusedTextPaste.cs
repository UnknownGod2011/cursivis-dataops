using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Windows.ApplicationModel.DataTransfer;

namespace Cursivis.Windows.Platform.Selection;

internal enum FocusedTextPasteStatus
{
    Pasted,
    Unsupported,
    Failed,
}

internal readonly record struct FocusedTextPasteResult(
    FocusedTextPasteStatus Status,
    string SafeDetail);

internal static class WindowsFocusedTextPaste
{
    private const uint WmPaste = 0x0302;
    private const uint AbortIfHung = 0x0002;

    public static FocusedTextPasteResult TryPaste(nint activeWindow, string text)
    {
        IDataObject? original = WindowsProtectedClipboardSelectionReader.CaptureOriginalClipboard();
        uint replacementSequence = 0;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            replacementSequence = GetClipboardSequenceNumber();

            GuiThreadInfo info = GuiThreadInfo.Create();
            uint threadId = GetWindowThreadProcessId(activeWindow, out _);
            if (!GetGUIThreadInfo(threadId, ref info) || info.FocusWindow == nint.Zero)
            {
                return new FocusedTextPasteResult(
                    FocusedTextPasteStatus.Unsupported,
                    "The focused text control could not be identified.");
            }

            nint sent = SendMessageTimeout(
                info.FocusWindow,
                WmPaste,
                0,
                0,
                AbortIfHung,
                300,
                out _);
            return sent == nint.Zero
                ? new FocusedTextPasteResult(
                    FocusedTextPasteStatus.Unsupported,
                    "The focused control did not accept deterministic text insertion.")
                : new FocusedTextPasteResult(FocusedTextPasteStatus.Pasted, "Text inserted.");
        }
        catch (COMException)
        {
            return new FocusedTextPasteResult(
                FocusedTextPasteStatus.Failed,
                "The Windows clipboard was unavailable during insertion.");
        }
        finally
        {
            WindowsProtectedClipboardSelectionReader.RestoreOriginalClipboard(
                original,
                replacementSequence);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint ActiveWindow;
        public nint FocusWindow;
        public nint CaptureWindow;
        public nint MenuOwnerWindow;
        public nint MoveSizeWindow;
        public nint CaretWindow;
        public Rect CaretRectangle;

        public static GuiThreadInfo Create() => new()
        {
            Size = (uint)Marshal.SizeOf<GuiThreadInfo>(),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
