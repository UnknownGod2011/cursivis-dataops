using Cursivis.Application.Realtime;
using Cursivis.Domain.Settings;
using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.Platform.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Pages;

public sealed partial class VoicePage : Page
{
    private bool _loading;

    public VoicePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (App.CurrentRuntime is not { } runtime)
        {
            return;
        }

        _loading = true;
        try
        {
            MicrophoneComboBox.Items.Clear();
            IReadOnlyList<WindowsMicrophoneEndpoint> endpoints = runtime.MicrophoneEndpoints;
            foreach (WindowsMicrophoneEndpoint endpoint in endpoints)
            {
                MicrophoneComboBox.Items.Add(new ComboBoxItem
                {
                    Content = endpoint.DisplayName,
                    Tag = endpoint.Id,
                });
            }

            string desired = runtime.CurrentSettings.Voice.DeviceEndpointId ?? "0";
            MicrophoneComboBox.SelectedItem = MicrophoneComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, desired, StringComparison.Ordinal))
                ?? MicrophoneComboBox.Items.FirstOrDefault();
            MicrophoneComboBox.IsEnabled = endpoints.Count > 0;
            AudioTestButton.IsEnabled = endpoints.Count > 0;
            MicrophonePermissionStatus.StatusText = endpoints.Count > 0
                ? ResourceText.Get("MicrophoneAvailableStatus")
                : ResourceText.Get("MicrophoneUnavailableStatus");
            MicrophonePermissionStatus.DetailText = endpoints.Count > 0
                ? ResourceText.Get("MicrophoneAvailableDetail")
                : ResourceText.Get("MicrophoneUnavailableDetail");

            if (AssistantVoiceComboBox.Items.FirstOrDefault() is ComboBoxItem voice)
            {
                voice.Tag = runtime.CurrentSettings.Voice.AssistantVoice;
            }
            if (VoiceLanguageComboBox.Items.FirstOrDefault() is ComboBoxItem language)
            {
                language.Tag = runtime.CurrentSettings.Voice.LanguageTag;
            }
            PauseSensitivitySlider.Value = runtime.CurrentSettings.Voice.PauseSensitivity switch
            {
                PauseSensitivity.Short => 4,
                PauseSensitivity.Patient => 0,
                _ => 2,
            };
            LiveTranscriptPanelToggle.IsOn = runtime.CurrentSettings.Voice.LiveTranscriptPanelVisible;
            RealtimeConnectionTestButton.IsEnabled = endpoints.Count > 0 && runtime.CredentialManager.HasSavedKey;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnPreferenceChanged(object sender, SelectionChangedEventArgs args) =>
        _ = PersistPreferencesAsync();

    private void OnPauseSensitivityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args) =>
        _ = PersistPreferencesAsync();

    private void OnLiveTranscriptPanelToggled(object sender, RoutedEventArgs args) =>
        _ = PersistPreferencesAsync();

    private async Task PersistPreferencesAsync()
    {
        if (_loading || App.CurrentRuntime is not { } runtime)
        {
            return;
        }

        string? endpoint = (MicrophoneComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        PauseSensitivity pause = PauseSensitivitySlider.Value switch
        {
            >= 3 => PauseSensitivity.Short,
            <= 1 => PauseSensitivity.Patient,
            _ => PauseSensitivity.Balanced,
        };
        try
        {
            await runtime.UpdateApplicationSettingsAsync(settings => settings with
            {
                Voice = settings.Voice with
                {
                    DeviceEndpointId = endpoint,
                    PauseSensitivity = pause,
                    LiveTranscriptPanelVisible = LiveTranscriptPanelToggle.IsOn,
                },
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MicrophonePermissionStatus.StatusText = ResourceText.Get("VoiceSettingsSaveFailedStatus");
        }
    }

    private async void OnAudioTestClicked(object sender, RoutedEventArgs args)
    {
        if (App.CurrentRuntime is not { } runtime)
        {
            return;
        }

        AudioTestButton.IsEnabled = false;
        MicrophoneLevelMeter.Value = 0;
        try
        {
            string? endpoint = (MicrophoneComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            float maximum = await runtime.TestMicrophoneAsync(endpoint, TimeSpan.FromSeconds(2));
            MicrophoneLevelMeter.Value = Math.Round(maximum * 100);
            MicrophonePermissionStatus.StatusText = ResourceText.Get("MicrophoneTestCompletedStatus");
            MicrophonePermissionStatus.DetailText = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                ResourceText.Get("MicrophonePeakLevelFormat"),
                Math.Round(maximum * 100));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            MicrophonePermissionStatus.StatusText = ResourceText.Get("MicrophoneTestFailedStatus");
            MicrophonePermissionStatus.DetailText = ResourceText.Get("MicrophoneTestFailedDetail");
        }
        finally
        {
            AudioTestButton.IsEnabled = MicrophoneComboBox.Items.Count > 0;
        }
    }

    private async void OnRealtimeConnectionTestClicked(object sender, RoutedEventArgs args)
    {
        if (App.CurrentRuntime is not { } runtime)
        {
            return;
        }

        RealtimeConnectionTestButton.IsEnabled = false;
        RealtimeConnectionStatus.StatusText = ResourceText.Get("RealtimeTestingStatus");
        try
        {
            LiveModeSnapshot snapshot = await runtime.TestRealtimeConnectionAsync();
            bool connected = snapshot.State is LiveModeState.Listening or LiveModeState.UserSpeaking;
            RealtimeConnectionStatus.StatusText = connected
                ? ResourceText.Get("RealtimeConnectedStatus")
                : ResourceText.Get("RealtimeConnectionFailedStatus");
            RealtimeConnectionStatus.DetailText = connected
                ? ResourceText.Get("RealtimeConnectedDetail")
                : snapshot.SafeError ?? ResourceText.Get("RealtimeConnectionFailedDetail");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            RealtimeConnectionStatus.StatusText = ResourceText.Get("RealtimeConnectionFailedStatus");
            RealtimeConnectionStatus.DetailText = ResourceText.Get("RealtimeConnectionFailedDetail");
        }
        finally
        {
            RealtimeConnectionTestButton.IsEnabled = runtime.CredentialManager.HasSavedKey &&
                                                      MicrophoneComboBox.Items.Count > 0;
        }
    }
}
