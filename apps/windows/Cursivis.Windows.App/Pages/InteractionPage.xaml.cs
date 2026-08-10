using Cursivis.Domain.Interaction;
using Cursivis.Domain.Settings;
using Cursivis.Windows.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Pages;

public sealed partial class InteractionPage : Page
{
    private bool _isInitialized;
    private bool _isLoading;

    public InteractionPage()
    {
        InitializeComponent();
        _isInitialized = true;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (App.CurrentRuntime is { } runtime)
        {
            Apply(runtime.CurrentSettings);
        }
    }

    private void OnSettingChanged(object sender, RoutedEventArgs args)
    {
        if (!_isInitialized || _isLoading)
        {
            return;
        }

        SaveState.IsDirty = true;
        SaveState.StateText = ResourceText.Get("SaveStateUnsavedChanges");
    }

    private void OnResetOrbPositionClicked(object sender, RoutedEventArgs args)
    {
        App.CurrentRuntime?.ResetOrbPlacement();
        SaveState.StateText = ResourceText.Get("OrbPositionResetCompleted");
    }

    private void OnDiscardRequested(object? sender, System.EventArgs args)
    {
        if (App.CurrentRuntime is { } runtime)
        {
            Apply(runtime.CurrentSettings);
        }
    }

    private async void OnSaveRequested(object? sender, System.EventArgs args)
    {
        if (App.CurrentRuntime is not { } runtime)
        {
            SaveState.StateText = ResourceText.Get("SaveStateSaveFailed");
            return;
        }

        try
        {
            ApplicationSettings saved = await runtime.SaveInteractionPreferencesAsync(
                new InteractionSettings(
                    GuidedModeOption.IsChecked == true ? InteractionMode.Guided : InteractionMode.Smart,
                    DisplayCaptureOption.IsChecked == true ? CaptureScope.FullDisplay : CaptureScope.ActiveWindow,
                    runtime.CurrentSettings.Interaction.ContextRetention,
                    CloseResultsAfterInsert: KeepResultsOpenToggle.IsOn is false),
                AlwaysShowOrbToggle.IsOn ? OrbVisibility.Always : OrbVisibility.DuringOperation,
                LaunchAtSignInToggle.IsOn);
            Apply(saved);
            SaveState.StateText = ResourceText.Get("SaveStateSaved");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            SaveState.StateText = ResourceText.Get("SaveStateSaveFailed");
        }
    }

    private void Apply(ApplicationSettings settings)
    {
        _isLoading = true;
        try
        {
            SmartModeOption.IsChecked = settings.Interaction.DefaultMode == InteractionMode.Smart;
            GuidedModeOption.IsChecked = settings.Interaction.DefaultMode == InteractionMode.Guided;
            AlwaysShowOrbToggle.IsOn = settings.Appearance.OrbVisibility == OrbVisibility.Always;
            KeepResultsOpenToggle.IsOn = !settings.Interaction.CloseResultsAfterInsert;
            ActiveWindowCaptureOption.IsChecked = settings.Interaction.CaptureScope == CaptureScope.ActiveWindow;
            DisplayCaptureOption.IsChecked = settings.Interaction.CaptureScope == CaptureScope.FullDisplay;
            LaunchAtSignInToggle.IsOn = settings.Startup.LaunchAtSignIn;
            SaveState.IsDirty = false;
            SaveState.StateText = ResourceText.Get("SaveStateNoChanges");
        }
        finally
        {
            _isLoading = false;
        }
    }
}
