using System;

namespace Cursivis.Windows.App.Models;

internal enum SettingsSection
{
    Overview,
    OpenAI,
    Interaction,
    QuickTask,
    Hotkeys,
    Voice,
    Browser,
    Privacy,
    Diagnostics,
    About,
}

internal sealed class SettingsSectionRequestedEventArgs(SettingsSection section) : EventArgs
{
    public SettingsSection Section { get; } = section;
}
