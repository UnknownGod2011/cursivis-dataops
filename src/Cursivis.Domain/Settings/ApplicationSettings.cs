using System.Collections.Immutable;
using Cursivis.Domain.Interaction;
using Cursivis.Domain.Models;
using Cursivis.Domain.QuickTasks;

namespace Cursivis.Domain.Settings;

public enum SettingsSection
{
    Overview,
    OpenAi,
    Interaction,
    CustomQuickTask,
    Hotkeys,
    Voice,
    BrowserIntegration,
    PrivacyAndSafety,
    Diagnostics,
    About,
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8,
}

public enum HotkeyCommand
{
    ContextTrigger,
    RealtimeLiveMode,
    DirectTakeAction,
    SmartDictation,
    CustomQuickTask,
    CancelEmergencyStop,
    OpenSettings,
}

public readonly record struct HotkeyChord
{
    public HotkeyChord(HotkeyModifiers modifiers, string key)
    {
        if (modifiers == HotkeyModifiers.None ||
            (modifiers & ~(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Meta)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modifiers), "At least one supported modifier is required.");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A non-modifier key is required.", nameof(key));
        }

        var normalizedKey = key.Trim().ToUpperInvariant();
        if (normalizedKey.Length > 24 || normalizedKey.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The hotkey key name is invalid.", nameof(key));
        }

        Modifiers = modifiers;
        Key = normalizedKey;
    }

    public HotkeyModifiers Modifiers { get; }

    public string Key { get; }

    public string Canonical => string.Join(
        '+',
        new[]
        {
            Modifiers.HasFlag(HotkeyModifiers.Control) ? "Ctrl" : null,
            Modifiers.HasFlag(HotkeyModifiers.Alt) ? "Alt" : null,
            Modifiers.HasFlag(HotkeyModifiers.Shift) ? "Shift" : null,
            Modifiers.HasFlag(HotkeyModifiers.Meta) ? "Win" : null,
            Key,
        }.Where(part => part is not null));

    public override string ToString() => Canonical;
}

public sealed class HotkeySettings
{
    public HotkeySettings(IEnumerable<KeyValuePair<HotkeyCommand, HotkeyChord>> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        Bindings = bindings.ToImmutableDictionary();
    }

    public ImmutableDictionary<HotkeyCommand, HotkeyChord> Bindings { get; }

    public HotkeyChord this[HotkeyCommand command] => Bindings[command];

    public static HotkeySettings CreateDefault() => new(
    [
        KeyValuePair.Create(HotkeyCommand.ContextTrigger, Chord("O")),
        KeyValuePair.Create(HotkeyCommand.RealtimeLiveMode, Chord("P")),
        KeyValuePair.Create(HotkeyCommand.DirectTakeAction, Chord("I")),
        KeyValuePair.Create(HotkeyCommand.SmartDictation, Chord("U")),
        KeyValuePair.Create(HotkeyCommand.CustomQuickTask, Chord("Y")),
        KeyValuePair.Create(HotkeyCommand.CancelEmergencyStop, Chord("ESCAPE")),
        KeyValuePair.Create(HotkeyCommand.OpenSettings, Chord("S")),
    ]);

    private static HotkeyChord Chord(string key) =>
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, key);
}

public sealed record ModelSelectionSettings(
    QualityProfile QualityProfile,
    ModelIdentifier ResponsesModel,
    ModelIdentifier RealtimeModel,
    ModelIdentifier TranscriptionModel)
{
    public static ModelSelectionSettings CreateDefault() => new(
        QualityProfile.Balanced,
        ModelCatalog.Balanced,
        ModelCatalog.Realtime,
        ModelCatalog.StreamingTranscription);
}

public enum PauseSensitivity
{
    Short,
    Balanced,
    Patient,
}

public sealed record VoiceAudioSettings(
    string? DeviceEndpointId,
    string LanguageTag,
    string AssistantVoice,
    PauseSensitivity PauseSensitivity,
    bool RealtimeEnabled,
    bool LiveTranscriptPanelVisible = true)
{
    public static VoiceAudioSettings CreateDefault() => new(
        DeviceEndpointId: null,
        LanguageTag: "en-US",
        AssistantVoice: "marin",
        PauseSensitivity.Balanced,
        RealtimeEnabled: true,
        LiveTranscriptPanelVisible: true);

    public override string ToString() =>
        $"VoiceAudioSettings(DeviceEndpointId={(DeviceEndpointId is null ? "system-default" : "<redacted>")}, LanguageTag={LanguageTag}, AssistantVoice={AssistantVoice}, PauseSensitivity={PauseSensitivity}, RealtimeEnabled={RealtimeEnabled}, LiveTranscriptPanelVisible={LiveTranscriptPanelVisible})";
}

public enum CaptureScope
{
    ActiveWindow,
    FullDisplay,
}

public enum ContextRetention
{
    OneMinute = 1,
    FiveMinutes = 5,
    FifteenMinutes = 15,
}

public sealed record InteractionSettings(
    InteractionMode DefaultMode,
    CaptureScope CaptureScope,
    ContextRetention ContextRetention,
    bool CloseResultsAfterInsert)
{
    public static InteractionSettings CreateDefault() => new(
        InteractionMode.Smart,
        CaptureScope.ActiveWindow,
        ContextRetention.FiveMinutes,
        CloseResultsAfterInsert: false);
}

public enum BrowserPermissionMode
{
    RequestPerSite,
}

public sealed record BrowserAutomationSettings(
    bool IntegrationEnabled,
    bool NativeMessagingEnabled,
    BrowserPermissionMode PermissionMode,
    bool RequireFreshConfirmationForSubmit)
{
    public static BrowserAutomationSettings CreateDefault() => new(
        IntegrationEnabled: false,
        NativeMessagingEnabled: true,
        BrowserPermissionMode.RequestPerSite,
        RequireFreshConfirmationForSubmit: true);
}

public sealed record PrivacySafetySettings(
    bool PersonalizationMemoryEnabled,
    bool AllowNavigationGuidanceCapture,
    int ContextRetentionMinutes,
    bool RequireConfirmationForHighImpactActions,
    bool IncludeRawContentInDiagnostics)
{
    public static PrivacySafetySettings CreateDefault() => new(
        PersonalizationMemoryEnabled: false,
        AllowNavigationGuidanceCapture: false,
        ContextRetentionMinutes: 5,
        RequireConfirmationForHighImpactActions: true,
        IncludeRawContentInDiagnostics: false);
}

public sealed record StartupSystemSettings(
    bool LaunchAtSignIn,
    bool StartMinimized,
    bool EnforceSingleInstance)
{
    public static StartupSystemSettings CreateDefault() => new(
        LaunchAtSignIn: false,
        StartMinimized: false,
        EnforceSingleInstance: true);
}

public enum ApplicationTheme
{
    System,
    Light,
    Dark,
}

public enum OrbVisibility
{
    Always,
    DuringOperation,
}

public sealed record AppearanceFeedbackSettings(
    ApplicationTheme Theme,
    OrbVisibility OrbVisibility,
    bool ReducedMotion,
    bool AudibleFeedback)
{
    public static AppearanceFeedbackSettings CreateDefault() => new(
        ApplicationTheme.System,
        OrbVisibility.DuringOperation,
        ReducedMotion: false,
        AudibleFeedback: true);
}

public enum DiagnosticsVerbosity
{
    ErrorsOnly,
    Normal,
    DetailedMetadata,
}

public sealed record DiagnosticsSettings(
    bool Enabled,
    DiagnosticsVerbosity Verbosity,
    int RetentionDays,
    bool IncludeRawUserContent)
{
    public static DiagnosticsSettings CreateDefault() => new(
        Enabled: true,
        DiagnosticsVerbosity.Normal,
        RetentionDays: 7,
        IncludeRawUserContent: false);
}

public sealed record ApplicationSettings(
    int SchemaVersion,
    ModelSelectionSettings Models,
    InteractionSettings Interaction,
    QuickTaskConfiguration QuickTask,
    HotkeySettings Hotkeys,
    VoiceAudioSettings Voice,
    BrowserAutomationSettings Browser,
    PrivacySafetySettings Privacy,
    StartupSystemSettings Startup,
    AppearanceFeedbackSettings Appearance,
    DiagnosticsSettings Diagnostics)
{
    public const int CurrentSchemaVersion = 1;

    public static ApplicationSettings CreateDefault() => new(
        CurrentSchemaVersion,
        ModelSelectionSettings.CreateDefault(),
        InteractionSettings.CreateDefault(),
        QuickTaskConfiguration.CreateDefault(),
        HotkeySettings.CreateDefault(),
        VoiceAudioSettings.CreateDefault(),
        BrowserAutomationSettings.CreateDefault(),
        PrivacySafetySettings.CreateDefault(),
        StartupSystemSettings.CreateDefault(),
        AppearanceFeedbackSettings.CreateDefault(),
        DiagnosticsSettings.CreateDefault());

    public override string ToString() =>
        $"ApplicationSettings(SchemaVersion={SchemaVersion}, Models={Models}, Interaction={Interaction}, QuickTask=<redacted>, Hotkeys={Hotkeys.Bindings.Count}, Voice={Voice}, Browser={Browser}, Privacy={Privacy}, Startup={Startup}, Appearance={Appearance}, Diagnostics={Diagnostics})";
}

public sealed record SettingsChangeSet(
    ImmutableHashSet<SettingsSection> Sections,
    ImmutableHashSet<string> DirtyPaths)
{
    public static SettingsChangeSet Empty { get; } = new([], []);
}
