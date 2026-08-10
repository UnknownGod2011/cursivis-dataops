using System.Globalization;
using Cursivis.Application.Dictation;
using Cursivis.Domain.Context;

namespace Cursivis.Windows.Platform.Foreground;

public sealed class WindowsDictationTargetProvider(
    IForegroundWindowIdentityProvider foreground) : IDictationTargetProvider
{
    private static readonly TimeSpan TargetLifetime = TimeSpan.FromMinutes(5);
    private readonly IForegroundWindowIdentityProvider _foreground = foreground
        ?? throw new ArgumentNullException(nameof(foreground));

    public Task<ContextSnapshot?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ForegroundWindowIdentity? current = _foreground.GetCurrent();
        if (current is null || current.WindowHandle == nint.Zero)
        {
            return Task.FromResult<ContextSnapshot?>(null);
        }

        string application = string.IsNullOrWhiteSpace(current.ProcessName)
            ? "windows-application"
            : current.ProcessName;
        var target = new TargetIdentity(
            application,
            current.WindowHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture));
        ContextSnapshot snapshot = ContextSnapshot.FromText(
            ContextKind.Text,
            ContextSource.DirectInput,
            target,
            "Smart Dictation insertion target",
            DateTimeOffset.UtcNow,
            TargetLifetime);
        return Task.FromResult<ContextSnapshot?>(snapshot);
    }
}
