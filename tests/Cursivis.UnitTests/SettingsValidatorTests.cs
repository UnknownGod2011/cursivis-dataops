using Cursivis.Domain.Models;
using Cursivis.Domain.QuickTasks;
using Cursivis.Domain.Settings;

namespace Cursivis.UnitTests;

public sealed class SettingsValidatorTests
{
    [Fact]
    public void CreateDefault_IsValidPrivacyPreservingAndUsesExactHotkeys()
    {
        var settings = ApplicationSettings.CreateDefault();

        var result = SettingsValidator.Validate(settings);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
        Assert.Empty(result.Issues);
        Assert.Equal("Ctrl+Alt+O", settings.Hotkeys[HotkeyCommand.ContextTrigger].Canonical);
        Assert.Equal("Ctrl+Alt+P", settings.Hotkeys[HotkeyCommand.RealtimeLiveMode].Canonical);
        Assert.Equal("Ctrl+Alt+I", settings.Hotkeys[HotkeyCommand.DirectTakeAction].Canonical);
        Assert.Equal("Ctrl+Alt+U", settings.Hotkeys[HotkeyCommand.SmartDictation].Canonical);
        Assert.Equal("Ctrl+Alt+Y", settings.Hotkeys[HotkeyCommand.CustomQuickTask].Canonical);
        Assert.Equal("Ctrl+Alt+ESCAPE", settings.Hotkeys[HotkeyCommand.CancelEmergencyStop].Canonical);
        Assert.False(settings.Privacy.PersonalizationMemoryEnabled);
        Assert.False(settings.Diagnostics.IncludeRawUserContent);
    }

    [Fact]
    public void Validate_DuplicateHotkeys_FailsClosed()
    {
        var settings = ApplicationSettings.CreateDefault();
        var duplicateBindings = settings.Hotkeys.Bindings.SetItem(
            HotkeyCommand.SmartDictation,
            settings.Hotkeys[HotkeyCommand.ContextTrigger]);

        var result = SettingsValidator.Validate(settings with
        {
            Hotkeys = new HotkeySettings(duplicateBindings),
        });

        AssertIssue(result, "hotkey.duplicate");
    }

    [Fact]
    public void Validate_MissingHotkey_FailsClosed()
    {
        var settings = ApplicationSettings.CreateDefault();
        var missingBinding = settings.Hotkeys.Bindings.Remove(HotkeyCommand.CustomQuickTask);

        var result = SettingsValidator.Validate(settings with
        {
            Hotkeys = new HotkeySettings(missingBinding),
        });

        AssertIssue(result, "hotkey.missing");
    }

    [Fact]
    public void Validate_UnknownHotkeyCommand_IsRejected()
    {
        var settings = ApplicationSettings.CreateDefault();
        var bindings = settings.Hotkeys.Bindings.Add(
            (HotkeyCommand)999,
            new HotkeyChord(HotkeyModifiers.Control | HotkeyModifiers.Alt, "V"));

        var result = SettingsValidator.Validate(settings with
        {
            Hotkeys = new HotkeySettings(bindings),
        });

        AssertIssue(result, "hotkey.command_unknown");
    }

    [Theory]
    [InlineData(HotkeyModifiers.Alt, "F4")]
    [InlineData(HotkeyModifiers.Alt, "TAB")]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, "DELETE")]
    [InlineData(HotkeyModifiers.Meta, "L")]
    public void Validate_ReservedHotkey_IsRejected(HotkeyModifiers modifiers, string key)
    {
        var settings = ApplicationSettings.CreateDefault();
        var bindings = settings.Hotkeys.Bindings.SetItem(
            HotkeyCommand.OpenSettings,
            new HotkeyChord(modifiers, key));

        var result = SettingsValidator.Validate(settings with
        {
            Hotkeys = new HotkeySettings(bindings),
        });

        AssertIssue(result, "hotkey.reserved");
    }

    [Fact]
    public void Validate_ModelRoleAndProfileMismatch_AreRejected()
    {
        var settings = ApplicationSettings.CreateDefault() with
        {
            Models = new ModelSelectionSettings(
                QualityProfile.Balanced,
                ModelCatalog.Realtime,
                ModelCatalog.Balanced,
                ModelCatalog.Economy),
        };

        var result = SettingsValidator.Validate(settings);

        AssertIssue(result, "model.role_mismatch");
        AssertIssue(result, "model.profile_mismatch");
    }

    [Fact]
    public void Validate_UnknownModel_IsRejectedWithoutFallback()
    {
        var settings = ApplicationSettings.CreateDefault() with
        {
            Models = ModelSelectionSettings.CreateDefault() with
            {
                RealtimeModel = new ModelIdentifier("unavailable-unlisted-model"),
            },
        };

        AssertIssue(SettingsValidator.Validate(settings), "model.unknown");
    }

    [Fact]
    public void Validate_UnapprovedQuickTask_CannotBeSaved()
    {
        var settings = ApplicationSettings.CreateDefault();
        var current = settings.QuickTask.Current;
        var unapproved = new QuickTaskDefinition(
            current.Id,
            current.DisplayName,
            current.FinalizedInstruction,
            current.SupportedContext,
            current.OutputMode,
            current.MayProposeAction,
            isExplicitlyApproved: false);

        var result = SettingsValidator.Validate(settings with
        {
            QuickTask = new QuickTaskConfiguration(unapproved),
        });

        AssertIssue(result, "quick_task.approval_required");
    }

    [Fact]
    public void Validate_BrowserSafetyBoundaries_CannotBeDisabled()
    {
        var settings = ApplicationSettings.CreateDefault() with
        {
            Browser = new BrowserAutomationSettings(
                IntegrationEnabled: true,
                NativeMessagingEnabled: false,
                BrowserPermissionMode.RequestPerSite,
                RequireFreshConfirmationForSubmit: false),
        };

        var result = SettingsValidator.Validate(settings);

        AssertIssue(result, "browser.transport_required");
        AssertIssue(result, "safety.submit_confirmation_required");
    }

    [Fact]
    public void Validate_PrivacySafetyBoundaries_CannotBeDisabled()
    {
        var settings = ApplicationSettings.CreateDefault() with
        {
            Privacy = new PrivacySafetySettings(
                PersonalizationMemoryEnabled: true,
                AllowNavigationGuidanceCapture: true,
                ContextRetentionMinutes: 90,
                RequireConfirmationForHighImpactActions: false,
                IncludeRawContentInDiagnostics: true),
        };

        var result = SettingsValidator.Validate(settings);

        AssertIssue(result, "privacy.retention_invalid");
        AssertIssue(result, "safety.confirmation_required");
        AssertIssue(result, "privacy.raw_diagnostics_forbidden");
    }

    [Fact]
    public void Validate_ContextRetentionMismatch_IsRejected()
    {
        var settings = ApplicationSettings.CreateDefault() with
        {
            Privacy = PrivacySafetySettings.CreateDefault() with { ContextRetentionMinutes = 15 },
        };

        AssertIssue(SettingsValidator.Validate(settings), "settings.context_retention_mismatch");
    }

    [Fact]
    public void Validate_DiagnosticsAndSingleInstanceGuardrails_AreEnforced()
    {
        var settings = ApplicationSettings.CreateDefault() with
        {
            Startup = new StartupSystemSettings(false, false, EnforceSingleInstance: false),
            Diagnostics = new DiagnosticsSettings(
                Enabled: true,
                DiagnosticsVerbosity.DetailedMetadata,
                RetentionDays: 0,
                IncludeRawUserContent: true),
        };

        var result = SettingsValidator.Validate(settings);

        AssertIssue(result, "startup.single_instance_required");
        AssertIssue(result, "diagnostics.retention_invalid");
        AssertIssue(result, "privacy.raw_diagnostics_forbidden");
    }

    [Fact]
    public void Validate_WhitespaceVoiceFields_AreRejected()
    {
        var settings = ApplicationSettings.CreateDefault() with
        {
            Voice = new VoiceAudioSettings(
                "endpoint-id",
                " ",
                " ",
                PauseSensitivity.Balanced,
                RealtimeEnabled: true),
        };

        var result = SettingsValidator.Validate(settings);

        AssertIssue(result, "voice.language_invalid");
        AssertIssue(result, "value.required");
    }

    [Fact]
    public void ApplicationSettings_ToString_DoesNotRevealQuickTaskInstructionOrDeviceId()
    {
        var settings = ApplicationSettings.CreateDefault() with
        {
            Voice = VoiceAudioSettings.CreateDefault() with { DeviceEndpointId = "private-device-id" },
        };

        var diagnostic = settings.ToString();

        Assert.DoesNotContain(QuickTaskDefaults.PromptOptimizerInstruction, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-device-id", diagnostic, StringComparison.Ordinal);
    }

    private static void AssertIssue(SettingsValidationResult result, string expectedCode)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }
}
