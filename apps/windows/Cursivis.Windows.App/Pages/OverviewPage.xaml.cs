using System;

using Cursivis.Windows.App.Models;
using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.Platform.Hotkeys;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Pages;

public sealed partial class OverviewPage : Page
{
    public OverviewPage()
    {
        InitializeComponent();
    }

    internal event EventHandler<SettingsSectionRequestedEventArgs>? NavigationRequested;

    internal void ApplyRuntimeStatus(AppRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        bool hasCredential = runtime.CredentialManager.HasSavedKey;
        bool hotkeysActive = IsActive(runtime.ContextHotkeyStatus) &&
                             IsActive(runtime.QuickTaskHotkeyStatus) &&
                             IsActive(runtime.DirectTakeActionHotkeyStatus) &&
                             IsActive(runtime.SmartDictationHotkeyStatus) &&
                             IsActive(runtime.LiveModeHotkeyStatus) &&
                             IsActive(runtime.CancelHotkeyStatus) &&
                             IsActive(runtime.SettingsHotkeyStatus);

        if (hasCredential)
        {
            OpenAiStatus.StatusText = ResourceText.Get("OverviewOpenAiReadyStatus");
            OpenAiStatus.DetailText = ResourceText.Get("OverviewOpenAiReadyDetail");
        }

        if (hotkeysActive)
        {
            HotkeyStatus.StatusText = ResourceText.Get("OverviewHotkeysReadyStatus");
            HotkeyStatus.DetailText = ResourceText.Get("OverviewHotkeysReadyDetail");
        }

        if (hasCredential && hotkeysActive)
        {
            OverviewHeader.StatusDetail = ResourceText.Get("OverviewBrowserPendingDetail");
        }
        else if (hotkeysActive)
        {
            OverviewHeader.StatusDetail = ResourceText.Get("OverviewOpenAiBrowserPendingDetail");
        }
        else if (hasCredential)
        {
            OverviewHeader.StatusDetail = ResourceText.Get("OverviewHotkeysBrowserPendingDetail");
        }

        StartupStatusText.Text = ResourceText.Get(
            runtime.IsLaunchAtSignInEnabled
                ? "OverviewStartupEnabled"
                : "OverviewStartupDisabled");
    }

    private static bool IsActive(HotkeyUpdateResult result) => result.Status is
        HotkeyUpdateStatus.Success or HotkeyUpdateStatus.AlreadyActive;

    private void OnConfigureOpenAiClicked(object sender, RoutedEventArgs args)
    {
        RequestNavigation(SettingsSection.OpenAI);
    }

    private void OnReviewHotkeysClicked(object sender, RoutedEventArgs args)
    {
        RequestNavigation(SettingsSection.Hotkeys);
    }

    private void OnSetUpBrowserClicked(object sender, RoutedEventArgs args)
    {
        RequestNavigation(SettingsSection.Browser);
    }

    private void RequestNavigation(SettingsSection section)
    {
        NavigationRequested?.Invoke(this, new SettingsSectionRequestedEventArgs(section));
    }
}
