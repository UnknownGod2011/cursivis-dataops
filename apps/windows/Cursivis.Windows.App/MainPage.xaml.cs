using System;
using System.Collections.Generic;

using Cursivis.Windows.App.Models;
using Cursivis.Windows.App.Pages;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App;

public sealed partial class MainPage : Page
{
    private readonly IReadOnlyDictionary<SettingsSection, Type> _pageTypes =
        new Dictionary<SettingsSection, Type>
        {
            [SettingsSection.Overview] = typeof(OverviewPage),
            [SettingsSection.OpenAI] = typeof(OpenAiPage),
            [SettingsSection.Interaction] = typeof(InteractionPage),
            [SettingsSection.QuickTask] = typeof(QuickTaskPage),
            [SettingsSection.Hotkeys] = typeof(HotkeysPage),
            [SettingsSection.Voice] = typeof(VoicePage),
            [SettingsSection.Browser] = typeof(BrowserIntegrationPage),
            [SettingsSection.Privacy] = typeof(PrivacySafetyPage),
            [SettingsSection.Diagnostics] = typeof(DiagnosticsPage),
            [SettingsSection.About] = typeof(AboutPage),
        };

    public MainPage()
    {
        InitializeComponent();
        NavigateTo(SettingsSection.Overview);
    }

    private void OnSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag
            && Enum.TryParse(tag, out SettingsSection section))
        {
            NavigateTo(section);
        }
    }

    private void NavigateTo(SettingsSection section)
    {
        if (!_pageTypes.TryGetValue(section, out Type? pageType))
        {
            return;
        }

        ContentFrame.Navigate(pageType);

        if (ContentFrame.Content is OverviewPage overviewPage)
        {
            overviewPage.NavigationRequested -= OnOverviewNavigationRequested;
            overviewPage.NavigationRequested += OnOverviewNavigationRequested;
            if (App.CurrentRuntime is AppRuntime runtime)
            {
                overviewPage.ApplyRuntimeStatus(runtime);
            }
        }
    }

    internal void RefreshRuntimeStatus()
    {
        if (ContentFrame.Content is OverviewPage overviewPage &&
            App.CurrentRuntime is AppRuntime runtime)
        {
            overviewPage.ApplyRuntimeStatus(runtime);
        }
    }

    private void OnOverviewNavigationRequested(
        object? sender,
        SettingsSectionRequestedEventArgs args)
    {
        foreach (object item in SettingsNavigation.MenuItems)
        {
            if (item is NavigationViewItem navigationItem
                && string.Equals(
                    navigationItem.Tag as string,
                    args.Section.ToString(),
                    StringComparison.Ordinal))
            {
                SettingsNavigation.SelectedItem = navigationItem;
                return;
            }
        }
    }
}
