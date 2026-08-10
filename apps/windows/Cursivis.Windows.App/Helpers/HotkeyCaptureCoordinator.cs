using Cursivis.Windows.App.Controls;

namespace Cursivis.Windows.App.Helpers;

/// <summary>
/// Suppresses command dispatch while a Settings recorder owns keyboard focus.
/// RegisterHotKey still raises WM_HOTKEY for an already-active chord; without
/// this guard, recording that chord could run its command instead of allowing
/// the page to explain that the candidate is unavailable or duplicated.
/// </summary>
internal static class HotkeyCaptureCoordinator
{
    private static readonly object Sync = new();
    private static HotkeyRecorder? _activeRecorder;

    public static bool IsCapturing
    {
        get
        {
            lock (Sync)
            {
                return _activeRecorder is not null;
            }
        }
    }

    public static bool TryBeginCapture(HotkeyRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        lock (Sync)
        {
            if (_activeRecorder is not null && !ReferenceEquals(_activeRecorder, recorder))
            {
                return false;
            }

            _activeRecorder = recorder;
            return true;
        }
    }

    public static void EndCapture(HotkeyRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        lock (Sync)
        {
            if (ReferenceEquals(_activeRecorder, recorder))
            {
                _activeRecorder = null;
            }
        }
    }

    public static bool TryCaptureRegisteredChord(string chord)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chord);
        HotkeyRecorder? recorder;
        lock (Sync)
        {
            recorder = _activeRecorder;
        }

        if (recorder is null)
        {
            return false;
        }

        recorder.CaptureRegisteredChord(chord);
        return true;
    }

    public static bool TryCancelCapture()
    {
        HotkeyRecorder? recorder;
        lock (Sync)
        {
            recorder = _activeRecorder;
        }

        if (recorder is null)
        {
            return false;
        }

        recorder.CancelActiveCapture();
        return true;
    }
}
