using System.Runtime.InteropServices;
using Cursivis.Application.Context;
using Windows.ApplicationModel.DataTransfer;

namespace Cursivis.Windows.Platform.ClipboardServices;

public sealed class WindowsResultClipboardService : IResultClipboardService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(80),
    ];

    public async Task<ResultClipboardWriteResult> CopyTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows clipboard requires Windows.");
        }

        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var package = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy,
                };
                package.SetText(text);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                return new ResultClipboardWriteResult(
                    ResultClipboardWriteStatus.Copied,
                    "The final result was copied to the clipboard.");
            }
            catch (COMException) when (attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(true);
            }
            catch (COMException)
            {
                return Failed();
            }
            catch (UnauthorizedAccessException)
            {
                return Failed();
            }
        }

        return Failed();
    }

    private static ResultClipboardWriteResult Failed() => new(
        ResultClipboardWriteStatus.Failed,
        "The result is ready, but the clipboard is currently unavailable.");
}
