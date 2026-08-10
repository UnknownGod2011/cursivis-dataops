using System;
using System.Globalization;
using System.Reflection;
using System.Diagnostics;

using Cursivis.Infrastructure.BrowserBridge;
using Cursivis.Infrastructure.Storage.Settings;
using Cursivis.Windows.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Cursivis.Windows.App.Pages;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        VersionText = GetVersionText();
        BuildCommitText = ResourceText.Get("BuildCommitNotEmbedded");
        DiagnosticsText = CreateSanitizedDiagnostics();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public string VersionText { get; }

    public string BuildCommitText { get; }

    public string DiagnosticsText { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs args) => RefreshDiagnostics(showNotice: false);

    private void OnRefreshClicked(object sender, RoutedEventArgs args) => RefreshDiagnostics(showNotice: true);

    private void RefreshDiagnostics(bool showNotice)
    {
        DiagnosticsText = CreateSanitizedDiagnostics();
        DiagnosticsTextBox.Text = DiagnosticsText;
        ApplyRuntimeHealth();
        if (!showNotice)
        {
            return;
        }

        DiagnosticsActionStatus.Title = ResourceText.Get("DiagnosticsRefreshedTitle");
        DiagnosticsActionStatus.Message = ResourceText.Get("DiagnosticsRefreshedMessage");
        DiagnosticsActionStatus.Severity = InfoBarSeverity.Success;
        DiagnosticsActionStatus.IsOpen = true;
    }

    private async void OnExportClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Cursivis Diagnostics");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(
                folder,
                $"cursivis-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, DiagnosticsTextBox.Text);
            DiagnosticsActionStatus.Title = ResourceText.Get("DiagnosticsExportedTitle");
            DiagnosticsActionStatus.Message = path;
            DiagnosticsActionStatus.Severity = InfoBarSeverity.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsActionStatus.Title = ResourceText.Get("DiagnosticsExportFailedTitle");
            DiagnosticsActionStatus.Message = ResourceText.Get("DiagnosticsExportFailedMessage");
            DiagnosticsActionStatus.Severity = InfoBarSeverity.Error;
        }

        DiagnosticsActionStatus.IsOpen = true;
    }

    private void OnOpenLogsClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            string logs = App.CurrentRuntime?.LogsDirectory
                ?? throw new InvalidOperationException("Logs directory unavailable.");
            Directory.CreateDirectory(logs);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logs}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticsActionStatus.Title = ResourceText.Get("DiagnosticsLogsFailedTitle");
            DiagnosticsActionStatus.Message = ResourceText.Get("DiagnosticsLogsFailedMessage");
            DiagnosticsActionStatus.Severity = InfoBarSeverity.Error;
            DiagnosticsActionStatus.IsOpen = true;
        }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            DataPackage package = new();
            package.SetText(DiagnosticsTextBox.Text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            DiagnosticsActionStatus.Title = ResourceText.Get("DiagnosticsCopiedTitle");
            DiagnosticsActionStatus.Message = ResourceText.Get("DiagnosticsCopiedMessage");
            DiagnosticsActionStatus.Severity = InfoBarSeverity.Success;
        }
        catch (Exception)
        {
            DiagnosticsActionStatus.Title = ResourceText.Get("DiagnosticsCopyFailedTitle");
            DiagnosticsActionStatus.Message = ResourceText.Get("DiagnosticsCopyFailedMessage");
            DiagnosticsActionStatus.Severity = InfoBarSeverity.Error;
        }

        DiagnosticsActionStatus.IsOpen = true;
    }

    private static string GetVersionText()
    {
        Version? version = typeof(DiagnosticsPage).Assembly.GetName().Version;
        return version?.ToString(3) ?? ResourceText.Get("VersionUnavailable");
    }

    private static string CreateSanitizedDiagnostics()
    {
        AppRuntime? runtime = App.CurrentRuntime;
        string generatedAt = DateTimeOffset.Now.ToString("G", CultureInfo.CurrentCulture);
        string openAi = runtime?.CredentialManager.HasSavedKey == true ? "Saved locally" : "Not configured";
        string storage = runtime?.ApplicationSettingsLoadStatus.ToString() ?? "Unavailable";
        string browser = runtime?.BrowserBridgeStatus.State.ToString() ?? "Unavailable";
        string hotkeys = runtime is not null && AllHotkeysRegistered(runtime) ? "Registered" : "Incomplete";
        string microphone = runtime is not null && runtime.MicrophoneEndpoints.Count > 0
            ? $"{runtime.MicrophoneEndpoints.Count} endpoint(s) available"
            : "No endpoint";
        return string.Join(
            Environment.NewLine,
            ResourceText.Get("DiagnosticsReportTitle"),
            $"{ResourceText.Get("DiagnosticsGeneratedAtLabel")}: {generatedAt}",
            $"{ResourceText.Get("DiagnosticsReportVersionLabel")}: {GetVersionText()}",
            $"{ResourceText.Get("DiagnosticsReportOpenAiLabel")}: {openAi}",
            $"Settings storage: {storage}",
            $"{ResourceText.Get("DiagnosticsReportBrowserLabel")}: {browser}",
            $"{ResourceText.Get("DiagnosticsReportHotkeysLabel")}: {hotkeys}",
            $"{ResourceText.Get("DiagnosticsReportMicrophoneLabel")}: {microphone}",
            "Screen capture: Ready; permission is checked only on an explicit capture",
            $"{ResourceText.Get("DiagnosticsReportSecretsLabel")}: {ResourceText.Get("DiagnosticsSecretsExcluded")}");
    }

    private void ApplyRuntimeHealth()
    {
        AppRuntime? runtime = App.CurrentRuntime;
        bool hasKey = runtime?.CredentialManager.HasSavedKey == true;
        DiagnosticsOpenAiStatus.StatusText = hasKey ? "OpenAI · Key saved" : "OpenAI · Not configured";
        DiagnosticsOpenAiStatus.DetailText = hasKey
            ? "The secret is stored in the current-user protected store; run the OpenAI page test to verify access."
            : "No local OpenAI key is available.";

        bool storageAvailable = runtime is not null &&
            runtime.ApplicationSettingsLoadStatus != SettingsLoadStatus.StorageUnavailable;
        DiagnosticsStorageStatus.StatusText = storageAvailable
            ? "Settings storage · Available"
            : "Settings storage · Unavailable";
        DiagnosticsStorageStatus.DetailText = runtime is null
            ? "Runtime unavailable"
            : runtime.ApplicationSettingsLoadStatus.ToString();

        bool hotkeys = runtime is not null && AllHotkeysRegistered(runtime);
        DiagnosticsHotkeysStatus.StatusText = hotkeys ? "Hotkeys · Registered" : "Hotkeys · Incomplete";
        DiagnosticsHotkeysStatus.DetailText = hotkeys
            ? "All seven production commands have active global chords."
            : "One or more global shortcuts could not be registered.";

        BrowserBridgeSnapshot browser = runtime?.BrowserBridgeStatus ?? BrowserBridgeSnapshot.Stopped;
        DiagnosticsBrowserStatus.StatusText = $"Browser bridge · {browser.State}";
        DiagnosticsBrowserStatus.DetailText = browser.SafeStatus;

        int microphoneCount = runtime?.MicrophoneEndpoints.Count ?? 0;
        DiagnosticsMicrophoneStatus.StatusText = microphoneCount > 0
            ? "Microphone · Available"
            : "Microphone · Not found";
        DiagnosticsMicrophoneStatus.DetailText = microphoneCount > 0
            ? $"{microphoneCount} Windows recording endpoint(s); use Voice to test capture."
            : "Windows reported no recording endpoints.";

        DiagnosticsCaptureStatus.StatusText = "Screen capture · Ready";
        DiagnosticsCaptureStatus.DetailText = "Permission and pixels are acquired only after an explicit trigger.";
    }

    private static bool AllHotkeysRegistered(AppRuntime runtime) =>
        new[]
        {
            AppRuntime.ContextTriggerCommand,
            AppRuntime.QuickTaskCommand,
            AppRuntime.DirectTakeActionCommand,
            AppRuntime.SmartDictationCommand,
            AppRuntime.RealtimeLiveModeCommand,
            AppRuntime.CancelCommand,
            AppRuntime.OpenSettingsCommand,
        }.All(command => !string.IsNullOrWhiteSpace(runtime.GetActiveHotkeyChord(command)));
}
