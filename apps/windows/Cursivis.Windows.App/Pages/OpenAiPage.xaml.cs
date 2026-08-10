using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Models;
using Cursivis.Domain.Settings;
using Cursivis.Infrastructure.Storage.Security;
using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.Platform.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Pages;

public sealed partial class OpenAiPage : Page
{
    private bool _loadingModels;

    public OpenAiPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AppRuntime? runtime = App.CurrentRuntime;
        OpenAiKeyInput.IsSecureStorageAvailable = runtime is not null && OperatingSystem.IsWindows();
        OpenAiKeyInput.HasSavedKey = runtime?.CredentialManager.HasSavedKey == true;
        if (OpenAiKeyInput.HasSavedKey)
        {
            OpenAiHeader.StatusText = ResourceText.Get("OpenAiSavedStatus");
            OpenAiHeader.StatusDetail = ResourceText.Get("OpenAiSavedStatusDetail");
        }

        if (runtime is not null)
        {
            ApplyModelSettings(runtime.CurrentSettings.Models);
        }
    }

    private async void OnQualityProfileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingModels || App.CurrentRuntime is not { } runtime ||
            QualityProfileComboBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        QualityProfile profile = (selected.Tag as string) switch
        {
            "Economy" => QualityProfile.Economy,
            "Best" => QualityProfile.BestQuality,
            _ => QualityProfile.Balanced,
        };
        ModelIdentifier responsesModel = ModelCatalog.ForProfile(profile).Id;
        await SaveModelsAsync(runtime, profile, responsesModel);
    }

    private async void OnAdvancedModelChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingModels || App.CurrentRuntime is not { } runtime ||
            ResponsesModelComboBox.SelectedItem is not ComboBoxItem responses ||
            RealtimeModelComboBox.SelectedItem is not ComboBoxItem realtime ||
            TranscriptionModelComboBox.SelectedItem is not ComboBoxItem transcription)
        {
            return;
        }

        await SaveModelsAsync(
            runtime,
            QualityProfileComboBox.SelectedItem is ComboBoxItem profileItem
                ? (profileItem.Tag as string) switch
                {
                    "Economy" => QualityProfile.Economy,
                    "Best" => QualityProfile.BestQuality,
                    _ => QualityProfile.Balanced,
                }
                : QualityProfile.Balanced,
            new ModelIdentifier((string)responses.Tag),
            new ModelIdentifier((string)realtime.Tag),
            new ModelIdentifier((string)transcription.Tag));
    }

    private async Task SaveModelsAsync(
        AppRuntime runtime,
        QualityProfile profile,
        ModelIdentifier responsesModel,
        ModelIdentifier? realtimeModel = null,
        ModelIdentifier? transcriptionModel = null)
    {
        try
        {
            ApplicationSettings saved = await runtime.UpdateApplicationSettingsAsync(settings => settings with
            {
                Models = settings.Models with
                {
                    QualityProfile = profile,
                    ResponsesModel = responsesModel,
                    RealtimeModel = realtimeModel ?? settings.Models.RealtimeModel,
                    TranscriptionModel = transcriptionModel ?? settings.Models.TranscriptionModel,
                },
            });
            ApplyModelSettings(saved.Models);
            ShowConnection(
                "Model preferences saved",
                "The selected models will be used after Cursivis restarts.",
                InfoBarSeverity.Success);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ApplyModelSettings(runtime.CurrentSettings.Models);
            ShowConnection(
                "Model preferences were not saved",
                "Cursivis kept the previous model configuration.",
                InfoBarSeverity.Error);
        }
    }

    private void ApplyModelSettings(ModelSelectionSettings models)
    {
        _loadingModels = true;
        try
        {
            SelectByTag(QualityProfileComboBox, models.QualityProfile switch
            {
                QualityProfile.Economy => "Economy",
                QualityProfile.BestQuality => "Best",
                _ => "Balanced",
            });
            SelectByTag(ResponsesModelComboBox, models.ResponsesModel.Value);
            SelectByTag(RealtimeModelComboBox, models.RealtimeModel.Value);
            SelectByTag(TranscriptionModelComboBox, models.TranscriptionModel.Value);
        }
        finally
        {
            _loadingModels = false;
        }
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        foreach (object item in comboBox.Items)
        {
            if (item is ComboBoxItem option && string.Equals(option.Tag as string, tag, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = option;
                return;
            }
        }
    }

    private async void OnSaveRequested(object? sender, EventArgs args)
    {
        AppRuntime? runtime = App.CurrentRuntime;
        if (runtime is null)
        {
            ShowStorageFailure();
            return;
        }

        string replacement = OpenAiKeyInput.TakeReplacementKey();
        char[] characters = replacement.ToCharArray();
        replacement = string.Empty;
        try
        {
            using var secret = new SecretBuffer(characters);
            await runtime.CredentialManager.SaveAsync(secret);
            OpenAiKeyInput.HasSavedKey = true;
            OpenAiHeader.StatusText = ResourceText.Get("OpenAiSavedStatus");
            OpenAiHeader.StatusDetail = ResourceText.Get("OpenAiSavedStatusDetail");
            ShowConnection(
                ResourceText.Get("OpenAiKeySavedTitle"),
                ResourceText.Get("OpenAiKeySavedMessage"),
                InfoBarSeverity.Success);
        }
        catch (SecretStoreException)
        {
            ShowStorageFailure();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }

    private async void OnDeleteRequested(object? sender, EventArgs args)
    {
        AppRuntime? runtime = App.CurrentRuntime;
        if (runtime is null)
        {
            ShowStorageFailure();
            return;
        }

        try
        {
            _ = await runtime.CredentialManager.DeleteAsync();
            OpenAiKeyInput.HasSavedKey = false;
            OpenAiKeyInput.ClearReplacementKey();
            OpenAiHeader.StatusText = ResourceText.Get("OpenAiNotConfiguredStatus");
            OpenAiHeader.StatusDetail = ResourceText.Get("OpenAiNotConfiguredStatusDetail");
            ShowConnection(
                ResourceText.Get("OpenAiKeyDeletedTitle"),
                ResourceText.Get("OpenAiKeyDeletedMessage"),
                InfoBarSeverity.Informational);
        }
        catch (SecretStoreException)
        {
            ShowStorageFailure();
        }
    }

    private async void OnTestRequested(object? sender, EventArgs args)
    {
        AppRuntime? runtime = App.CurrentRuntime;
        if (runtime is null)
        {
            ShowStorageFailure();
            return;
        }

        ShowConnection(
            ResourceText.Get("OpenAiTestingTitle"),
            ResourceText.Get("OpenAiTestingMessage"),
            InfoBarSeverity.Informational);
        ModelAvailabilityResult availability = await runtime.ResponsesGateway
            .CheckModelAvailabilityAsync(ModelCatalog.Balanced.Value);
        if (availability.Available)
        {
            OpenAiHeader.StatusText = ResourceText.Get("OpenAiConnectedStatus");
            OpenAiHeader.StatusDetail = ResourceText.Get("OpenAiConnectedStatusDetail");
            ShowConnection(
                ResourceText.Get("OpenAiConnectedTitle"),
                ResourceText.Get("OpenAiConnectedMessage"),
                InfoBarSeverity.Success);
            return;
        }

        ShowConnection(
            ResourceText.Get("OpenAiTestFailedTitle"),
            GetFailureMessage(availability.Failure?.Kind),
            InfoBarSeverity.Error);
    }

    private void ShowStorageFailure() => ShowConnection(
        ResourceText.Get("OpenAiStorageFailureTitle"),
        ResourceText.Get("OpenAiStorageFailureMessage"),
        InfoBarSeverity.Error);

    private void ShowConnection(string title, string message, InfoBarSeverity severity)
    {
        ConnectionInfo.Title = title;
        ConnectionInfo.Message = message;
        ConnectionInfo.Severity = severity;
        ConnectionInfo.IsOpen = true;
    }

    private static string GetFailureMessage(OpenAiFailureKind? kind) => kind switch
    {
        OpenAiFailureKind.Authentication => ResourceText.Get("OpenAiAuthenticationFailureMessage"),
        OpenAiFailureKind.Permission => ResourceText.Get("OpenAiPermissionFailureMessage"),
        OpenAiFailureKind.ModelUnavailable => ResourceText.Get("OpenAiModelFailureMessage"),
        OpenAiFailureKind.Quota => ResourceText.Get("OpenAiQuotaFailureMessage"),
        OpenAiFailureKind.RateLimit => ResourceText.Get("OpenAiRateLimitFailureMessage"),
        OpenAiFailureKind.Network => ResourceText.Get("OpenAiNetworkFailureMessage"),
        OpenAiFailureKind.Timeout => ResourceText.Get("OpenAiTimeoutFailureMessage"),
        _ => ResourceText.Get("OpenAiUnknownFailureMessage"),
    };
}
