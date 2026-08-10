using System.Collections.Immutable;
using Cursivis.Domain.Common;
using Cursivis.Domain.Models;
using Cursivis.Domain.QuickTasks;

namespace Cursivis.Domain.Settings;

public sealed class SettingsValidationResult : ValidationResult
{
    public SettingsValidationResult(IEnumerable<ValidationIssue> issues)
        : base(issues)
    {
    }
}

public static class SettingsValidator
{
    private static readonly ImmutableHashSet<string> ModifierOnlyKeys =
        new[] { "CTRL", "CONTROL", "ALT", "SHIFT", "WIN", "WINDOWS", "META" }
            .ToImmutableHashSet(StringComparer.Ordinal);

    public static SettingsValidationResult Validate(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();

        if (settings.SchemaVersion != ApplicationSettings.CurrentSchemaVersion)
        {
            Add(issues, "schemaVersion", "schema.unsupported", "The settings schema version is unsupported.");
        }

        ValidateModels(settings.Models, issues);
        ValidateInteraction(settings.Interaction, issues);
        ValidateQuickTask(settings.QuickTask, issues);
        ValidateHotkeys(settings.Hotkeys, issues);
        ValidateVoice(settings.Voice, issues);
        ValidateBrowser(settings.Browser, issues);
        ValidatePrivacy(settings.Privacy, issues);
        ValidateStartup(settings.Startup, issues);
        ValidateAppearance(settings.Appearance, issues);
        ValidateDiagnostics(settings.Diagnostics, issues);
        ValidateCrossSettingConstraints(settings, issues);

        return new(issues);
    }

    private static void ValidateModels(
        ModelSelectionSettings? models,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (models is null)
        {
            Add(issues, "models", "value.required", "Model selection settings are required.");
            return;
        }

        if (!Enum.IsDefined(models.QualityProfile))
        {
            Add(issues, "models.qualityProfile", "enum.invalid", "The quality profile is invalid.");
        }

        ValidateModelRole(models.ResponsesModel, ModelRole.Responses, "models.responsesModel", issues);
        ValidateModelRole(models.RealtimeModel, ModelRole.Realtime, "models.realtimeModel", issues);
        ValidateModelRole(models.TranscriptionModel, ModelRole.Transcription, "models.transcriptionModel", issues);

        var profileModel = Enum.IsDefined(models.QualityProfile)
            ? ModelCatalog.ForProfile(models.QualityProfile)
            : null;

        if (profileModel is not null && models.ResponsesModel != profileModel.Id)
        {
            Add(
                issues,
                "models.responsesModel",
                "model.profile_mismatch",
                "The Responses model does not match the selected quality profile.");
        }
    }

    private static void ValidateModelRole(
        ModelIdentifier identifier,
        ModelRole expectedRole,
        string path,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var descriptor = ModelCatalog.Find(identifier);
        if (descriptor is null)
        {
            Add(issues, path, "model.unknown", "The model identifier is not in the supported catalog.");
        }
        else if (descriptor.Role != expectedRole)
        {
            Add(issues, path, "model.role_mismatch", $"The selected model does not support the {expectedRole} role.");
        }
    }

    private static void ValidateInteraction(
        InteractionSettings? interaction,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (interaction is null)
        {
            Add(issues, "interaction", "value.required", "Interaction settings are required.");
            return;
        }

        ValidateEnum(interaction.DefaultMode, "interaction.defaultMode", issues);
        ValidateEnum(interaction.CaptureScope, "interaction.captureScope", issues);
        ValidateEnum(interaction.ContextRetention, "interaction.contextRetention", issues);
    }

    private static void ValidateQuickTask(
        QuickTaskConfiguration? configuration,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (configuration?.Current is null)
        {
            Add(issues, "quickTask.current", "value.required", "A current Quick Task definition is required.");
            return;
        }

        var task = configuration.Current;
        if (string.IsNullOrWhiteSpace(task.Id.Value))
        {
            Add(issues, "quickTask.current.id", "value.required", "A Quick Task identifier is required.");
        }

        if (string.IsNullOrWhiteSpace(task.DisplayName))
        {
            Add(issues, "quickTask.current.displayName", "value.required", "A Quick Task display name is required.");
        }

        if (string.IsNullOrWhiteSpace(task.FinalizedInstruction))
        {
            Add(issues, "quickTask.current.finalizedInstruction", "value.required", "A finalized instruction is required.");
        }

        if (task.SupportedContext is QuickTaskContextType.None ||
            (task.SupportedContext & ~QuickTaskContextType.TextAndImage) != 0)
        {
            Add(issues, "quickTask.current.supportedContext", "quick_task.context_invalid", "The supported context is invalid.");
        }

        if (!Enum.IsDefined(task.OutputMode))
        {
            Add(issues, "quickTask.current.outputMode", "enum.invalid", "The Quick Task output mode is invalid.");
        }

        if (!task.IsExplicitlyApproved)
        {
            Add(issues, "quickTask.current.isExplicitlyApproved", "quick_task.approval_required", "The finalized Quick Task must be explicitly approved before saving.");
        }
    }

    private static void ValidateHotkeys(
        HotkeySettings? hotkeys,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (hotkeys is null)
        {
            Add(issues, "hotkeys", "value.required", "Hotkey settings are required.");
            return;
        }

        foreach (var command in Enum.GetValues<HotkeyCommand>())
        {
            if (!hotkeys.Bindings.TryGetValue(command, out var chord))
            {
                Add(issues, $"hotkeys.{command}", "hotkey.missing", "Every command must have a configured hotkey.");
                continue;
            }

            ValidateChord(command, chord, issues);
        }

        foreach (var binding in hotkeys.Bindings.Where(binding => !Enum.IsDefined(binding.Key)))
        {
            Add(
                issues,
                $"hotkeys.{(int)binding.Key}",
                "hotkey.command_unknown",
                "The hotkey command is unsupported.");
        }

        foreach (var duplicate in hotkeys.Bindings
                     .GroupBy(binding => binding.Value.Canonical, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            Add(
                issues,
                "hotkeys",
                "hotkey.duplicate",
                $"The shortcut {duplicate.Key} is assigned to more than one command.");
        }
    }

    private static void ValidateChord(
        HotkeyCommand command,
        HotkeyChord chord,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var path = $"hotkeys.{command}";

        if (chord.Modifiers == HotkeyModifiers.None || ModifierOnlyKeys.Contains(chord.Key))
        {
            Add(issues, path, "hotkey.modifier_only", "A shortcut must include a non-modifier key.");
        }

        if (IsReserved(chord))
        {
            Add(issues, path, "hotkey.reserved", "This shortcut is reserved or dangerous and cannot be used.");
        }
    }

    private static bool IsReserved(HotkeyChord chord) =>
        (chord.Modifiers == HotkeyModifiers.Alt && chord.Key is "F4" or "TAB") ||
        (chord.Modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt) && chord.Key == "DELETE") ||
        (chord.Modifiers == HotkeyModifiers.Meta && chord.Key is "L" or "D");

    private static void ValidateVoice(
        VoiceAudioSettings? voice,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (voice is null)
        {
            Add(issues, "voice", "value.required", "Voice and audio settings are required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(voice.LanguageTag) || voice.LanguageTag.Length > 35)
        {
            Add(issues, "voice.languageTag", "voice.language_invalid", "A valid language tag is required.");
        }

        if (string.IsNullOrWhiteSpace(voice.AssistantVoice))
        {
            Add(issues, "voice.assistantVoice", "value.required", "An assistant voice is required.");
        }

        ValidateEnum(voice.PauseSensitivity, "voice.pauseSensitivity", issues);
    }

    private static void ValidateBrowser(
        BrowserAutomationSettings? browser,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (browser is null)
        {
            Add(issues, "browser", "value.required", "Browser integration settings are required.");
            return;
        }

        ValidateEnum(browser.PermissionMode, "browser.permissionMode", issues);

        if (browser.IntegrationEnabled && !browser.NativeMessagingEnabled)
        {
            Add(issues, "browser.nativeMessagingEnabled", "browser.transport_required", "Browser integration requires Native Messaging.");
        }

        if (!browser.RequireFreshConfirmationForSubmit)
        {
            Add(issues, "browser.requireFreshConfirmationForSubmit", "safety.submit_confirmation_required", "Form submission must always require fresh confirmation.");
        }
    }

    private static void ValidatePrivacy(
        PrivacySafetySettings? privacy,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (privacy is null)
        {
            Add(issues, "privacy", "value.required", "Privacy and safety settings are required.");
            return;
        }

        if (privacy.ContextRetentionMinutes is < 1 or > 15)
        {
            Add(issues, "privacy.contextRetentionMinutes", "privacy.retention_invalid", "Context retention must be between 1 and 15 minutes.");
        }

        if (!privacy.RequireConfirmationForHighImpactActions)
        {
            Add(issues, "privacy.requireConfirmationForHighImpactActions", "safety.confirmation_required", "High-impact confirmation cannot be disabled.");
        }

        if (privacy.IncludeRawContentInDiagnostics)
        {
            Add(issues, "privacy.includeRawContentInDiagnostics", "privacy.raw_diagnostics_forbidden", "Raw user content cannot be included in diagnostics.");
        }
    }

    private static void ValidateStartup(
        StartupSystemSettings? startup,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (startup is null)
        {
            Add(issues, "startup", "value.required", "Startup and system settings are required.");
            return;
        }

        if (!startup.EnforceSingleInstance)
        {
            Add(issues, "startup.enforceSingleInstance", "startup.single_instance_required", "Cursivis must use one authoritative resident process.");
        }
    }

    private static void ValidateAppearance(
        AppearanceFeedbackSettings? appearance,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (appearance is null)
        {
            Add(issues, "appearance", "value.required", "Appearance and feedback settings are required.");
            return;
        }

        ValidateEnum(appearance.Theme, "appearance.theme", issues);
        ValidateEnum(appearance.OrbVisibility, "appearance.orbVisibility", issues);
    }

    private static void ValidateDiagnostics(
        DiagnosticsSettings? diagnostics,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (diagnostics is null)
        {
            Add(issues, "diagnostics", "value.required", "Diagnostics settings are required.");
            return;
        }

        ValidateEnum(diagnostics.Verbosity, "diagnostics.verbosity", issues);

        if (diagnostics.RetentionDays is < 1 or > 30)
        {
            Add(issues, "diagnostics.retentionDays", "diagnostics.retention_invalid", "Diagnostics retention must be between 1 and 30 days.");
        }

        if (diagnostics.IncludeRawUserContent)
        {
            Add(issues, "diagnostics.includeRawUserContent", "privacy.raw_diagnostics_forbidden", "Diagnostics cannot include raw user content.");
        }
    }

    private static void ValidateCrossSettingConstraints(
        ApplicationSettings settings,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (settings.Interaction is not null &&
            settings.Privacy is not null &&
            Enum.IsDefined(settings.Interaction.ContextRetention) &&
            (int)settings.Interaction.ContextRetention != settings.Privacy.ContextRetentionMinutes)
        {
            Add(
                issues,
                "privacy.contextRetentionMinutes",
                "settings.context_retention_mismatch",
                "Interaction and privacy context-retention settings must match.");
        }
    }

    private static void ValidateEnum<T>(
        T value,
        string path,
        ImmutableArray<ValidationIssue>.Builder issues)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            Add(issues, path, "enum.invalid", "The configured value is invalid.");
        }
    }

    private static void Add(
        ImmutableArray<ValidationIssue>.Builder issues,
        string path,
        string code,
        string message) =>
        issues.Add(new(path, code, message));
}
