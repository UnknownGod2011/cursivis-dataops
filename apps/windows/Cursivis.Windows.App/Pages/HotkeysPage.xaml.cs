using System;
using System.Collections.Generic;
using System.Linq;

using Cursivis.Windows.App.Controls;
using Cursivis.Windows.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Cursivis.Windows.Platform.Hotkeys;

namespace Cursivis.Windows.App.Pages;

public sealed partial class HotkeysPage : Page
{
    public HotkeysPage()
    {
        InitializeComponent();
        ShowRuntimeRegistrations();
    }

    private void ShowRuntimeRegistrations()
    {
        AppRuntime? runtime = App.CurrentRuntime;
        if (runtime is null)
        {
            return;
        }

        var registrations = new[]
        {
            new RuntimeRegistration(ContextTriggerRecorder, AppRuntime.ContextTriggerCommand, runtime.ContextHotkeyStatus),
            new RuntimeRegistration(LiveModeRecorder, AppRuntime.RealtimeLiveModeCommand, runtime.LiveModeHotkeyStatus),
            new RuntimeRegistration(TakeActionRecorder, AppRuntime.DirectTakeActionCommand, runtime.DirectTakeActionHotkeyStatus),
            new RuntimeRegistration(DictationRecorder, AppRuntime.SmartDictationCommand, runtime.SmartDictationHotkeyStatus),
            new RuntimeRegistration(QuickTaskRecorder, AppRuntime.QuickTaskCommand, runtime.QuickTaskHotkeyStatus),
            new RuntimeRegistration(CancelRecorder, AppRuntime.CancelCommand, runtime.CancelHotkeyStatus),
            new RuntimeRegistration(SettingsRecorder, AppRuntime.OpenSettingsCommand, runtime.SettingsHotkeyStatus),
        };

        foreach (RuntimeRegistration registration in registrations)
        {
            registration.Recorder.ConfiguredChord = runtime.GetConfiguredHotkeyChord(registration.Command);
            registration.Recorder.ActiveChord = runtime.GetActiveHotkeyChord(registration.Command);
            registration.Recorder.StatusText = IsActive(registration.Result)
                ? ResourceText.Get("HotkeyRegisteredStatus")
                : DescribeFailure(registration.Result);
        }

        if (runtime.StartupHotkeyFallbacks.Count > 0)
        {
            HotkeysHeader.StatusText = "Shortcut conflict resolved";
            HotkeysHeader.StatusDetail = "Cursivis Next kept every command available with a visible fallback.";
            RegistrationInfo.Severity = InfoBarSeverity.Warning;
            RegistrationInfo.Title = "A default shortcut was already in use";
            RegistrationInfo.Message = string.Join(
                " ",
                runtime.StartupHotkeyFallbacks.Select(static fallback =>
                    $"{fallback.Key}: {fallback.Value.RequestedChord} was changed to {fallback.Value.ActiveChord}."));
            return;
        }

        if (registrations.All(static registration => IsActive(registration.Result)))
        {
            HotkeysHeader.StatusText = ResourceText.Get("HotkeysActiveStatus");
            HotkeysHeader.StatusDetail = ResourceText.Get("HotkeysActiveStatusDetail");
            RegistrationInfo.Severity = InfoBarSeverity.Success;
            RegistrationInfo.Title = ResourceText.Get("HotkeysActiveInfoTitle");
            RegistrationInfo.Message = ResourceText.Get("HotkeysActiveInfoMessage");
            return;
        }

        HotkeysHeader.StatusText = "Shortcut registration needs attention";
        HotkeysHeader.StatusDetail = "Commands with an error remain inactive until you choose a different chord.";
        RegistrationInfo.Severity = InfoBarSeverity.Error;
        RegistrationInfo.Title = "One or more global shortcuts are unavailable";
        RegistrationInfo.Message = string.Join(
            " ",
            registrations
                .Where(static registration => !IsActive(registration.Result))
                .Select(static registration =>
                    $"{registration.Recorder.FunctionName}: {DescribeFailure(registration.Result)}"));
    }

    private IReadOnlyList<HotkeyRecorder> Recorders =>
    [
        ContextTriggerRecorder,
        LiveModeRecorder,
        TakeActionRecorder,
        DictationRecorder,
        QuickTaskRecorder,
        CancelRecorder,
        SettingsRecorder,
    ];

    private async void OnCandidateCaptured(object? sender, HotkeyCandidateEventArgs args)
    {
        if (sender is not HotkeyRecorder recorder)
        {
            return;
        }

        bool duplicatesAnotherCommand = Recorders.Any(
            item => !ReferenceEquals(item, recorder) &&
                    string.Equals(item.ConfiguredChord, args.Chord, StringComparison.Ordinal));
        if (duplicatesAnotherCommand)
        {
            recorder.SetExternalValidation(ResourceText.Get("HotkeyDuplicateValidation"));
            HotkeyPageStatus.Text = ResourceText.Get("HotkeyDuplicatePageStatus");
            return;
        }

        AppRuntime? runtime = App.CurrentRuntime;
        string? command = GetCommand(recorder);
        if (runtime is null || command is null)
        {
            HotkeyPageStatus.Text = ResourceText.Get("HotkeyCandidatePageStatus");
            return;
        }

        try
        {
            HotkeyUpdateResult result = await runtime.UpdateHotkeyAsync(command, args.Chord);
            if (result.Status is HotkeyUpdateStatus.Success or HotkeyUpdateStatus.AlreadyActive)
            {
                recorder.ConfiguredChord = runtime.GetConfiguredHotkeyChord(command);
                recorder.ActiveChord = result.ActiveChord?.ToString() ?? args.Chord;
                recorder.StatusText = ResourceText.Get("HotkeyRegisteredStatus");
                HotkeyPageStatus.Text = ResourceText.Get("HotkeyRegistrationSucceededPageStatus");
                return;
            }

            recorder.ConfiguredChord = runtime.GetConfiguredHotkeyChord(command);
            recorder.ActiveChord = runtime.GetActiveHotkeyChord(command);
            recorder.StatusText = DescribeFailure(result);
            HotkeyPageStatus.Text = recorder.StatusText;
        }
        catch (ArgumentException)
        {
            if (runtime is not null && command is not null)
            {
                recorder.ConfiguredChord = runtime.GetConfiguredHotkeyChord(command);
                recorder.ActiveChord = runtime.GetActiveHotkeyChord(command);
            }
            recorder.SetExternalValidation(ResourceText.Get("HotkeyReservedConflict"));
            HotkeyPageStatus.Text = ResourceText.Get("HotkeyRegistrationFailedPageStatus");
        }
    }

    private async void OnRestoreDefaultsClicked(object sender, RoutedEventArgs args)
    {
        (HotkeyRecorder Recorder, string Chord)[] defaults =
        [
            (ContextTriggerRecorder, "Ctrl+Alt+O"),
            (LiveModeRecorder, "Ctrl+Alt+P"),
            (TakeActionRecorder, "Ctrl+Alt+I"),
            (DictationRecorder, "Ctrl+Alt+U"),
            (QuickTaskRecorder, "Ctrl+Alt+Y"),
            (CancelRecorder, "Ctrl+Alt+Escape"),
            (SettingsRecorder, "Ctrl+Alt+S"),
        ];
        AppRuntime? runtime = App.CurrentRuntime;
        bool allRegistered = runtime is not null;
        foreach ((HotkeyRecorder recorder, string chord) in defaults)
        {
            string? command = GetCommand(recorder);
            if (runtime is null || command is null)
            {
                allRegistered = false;
                continue;
            }

            HotkeyUpdateResult result = await runtime.UpdateHotkeyAsync(command, chord);
            bool registered = result.Status is HotkeyUpdateStatus.Success or HotkeyUpdateStatus.AlreadyActive;
            allRegistered &= registered;
            recorder.ConfiguredChord = runtime.GetConfiguredHotkeyChord(command);
            recorder.ActiveChord = result.ActiveChord?.ToString() ?? runtime.GetActiveHotkeyChord(command);
            recorder.StatusText = registered
                ? ResourceText.Get("HotkeyRegisteredStatus")
                : DescribeFailure(result);
        }

        HotkeyPageStatus.Text = ResourceText.Get(
            allRegistered
                ? "HotkeyDefaultsRegisteredStatus"
                : "HotkeyRegistrationFailedPageStatus");
    }

    private string? GetCommand(HotkeyRecorder recorder)
    {
        if (ReferenceEquals(recorder, ContextTriggerRecorder))
        {
            return AppRuntime.ContextTriggerCommand;
        }

        if (ReferenceEquals(recorder, LiveModeRecorder))
        {
            return AppRuntime.RealtimeLiveModeCommand;
        }

        if (ReferenceEquals(recorder, TakeActionRecorder))
        {
            return AppRuntime.DirectTakeActionCommand;
        }

        if (ReferenceEquals(recorder, DictationRecorder))
        {
            return AppRuntime.SmartDictationCommand;
        }

        if (ReferenceEquals(recorder, QuickTaskRecorder))
        {
            return AppRuntime.QuickTaskCommand;
        }

        if (ReferenceEquals(recorder, CancelRecorder))
        {
            return AppRuntime.CancelCommand;
        }

        return ReferenceEquals(recorder, SettingsRecorder)
            ? AppRuntime.OpenSettingsCommand
            : null;
    }

    private static bool IsActive(HotkeyUpdateResult result) => result.Status is
        HotkeyUpdateStatus.Success or HotkeyUpdateStatus.AlreadyActive;

    private static string DescribeFailure(HotkeyUpdateResult result) => result.Status switch
    {
        HotkeyUpdateStatus.Conflict =>
            $"{result.ProposedChord} is already registered by another application. Choose a different shortcut.",
        HotkeyUpdateStatus.PersistenceFailed =>
            "Windows activated the shortcut, but Cursivis Next could not save it safely. The previous shortcut was restored.",
        HotkeyUpdateStatus.RollbackFailed =>
            $"Windows could not safely replace this shortcut (error {result.NativeErrorCode}). Restart Cursivis Next and choose another chord.",
        HotkeyUpdateStatus.NativeRegistrationFailed =>
            $"Windows rejected {result.ProposedChord} (error {result.NativeErrorCode}). Choose a different shortcut.",
        HotkeyUpdateStatus.NativeReleaseFailed =>
            $"Windows could not release the previous shortcut (error {result.NativeErrorCode}). Restart Cursivis Next before trying again.",
        HotkeyUpdateStatus.NotRegistered =>
            "The shortcut runtime is unavailable. Restart Cursivis Next.",
        _ => "The shortcut could not be activated. The previous shortcut remains active.",
    };

    private sealed record RuntimeRegistration(
        HotkeyRecorder Recorder,
        string Command,
        HotkeyUpdateResult Result);
}
