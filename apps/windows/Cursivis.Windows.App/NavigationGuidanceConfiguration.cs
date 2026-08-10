using Cursivis.Domain.Settings;

namespace Cursivis.Windows.App;

internal readonly record struct NavigationGuidanceConfigurationSnapshot(
    bool Enabled,
    CaptureScope CaptureScope);

internal sealed class NavigationGuidanceConfiguration(
    bool enabled,
    CaptureScope captureScope)
{
    private readonly object _gate = new();
    private NavigationGuidanceConfigurationSnapshot _snapshot = new(enabled, captureScope);

    public NavigationGuidanceConfigurationSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void Update(bool enabled, CaptureScope captureScope)
    {
        if (!Enum.IsDefined(captureScope))
        {
            throw new ArgumentOutOfRangeException(nameof(captureScope));
        }

        lock (_gate)
        {
            _snapshot = new NavigationGuidanceConfigurationSnapshot(enabled, captureScope);
        }
    }
}
