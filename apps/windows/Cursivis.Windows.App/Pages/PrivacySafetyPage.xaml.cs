using Cursivis.Application.Realtime;
using Cursivis.Domain.Settings;
using Cursivis.Windows.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Pages;

public sealed partial class PrivacySafetyPage : Page
{
    private bool _loadingMemory;
    private bool _loadingSettings;

    public PrivacySafetyPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ApplyPrivacySettings();
        await RefreshMemoryStateAsync();
    }

    private async void OnMemoryToggled(object sender, RoutedEventArgs args)
    {
        if (_loadingMemory || App.CurrentRuntime is not { } runtime)
        {
            return;
        }

        try
        {
            bool requested = PersonalizationMemoryToggle.IsOn;
            bool previous = (await runtime.LiveModeMemory.GetAsync()).IsEnabled;
            await runtime.UpdateApplicationSettingsAsync(settings => settings with
            {
                Privacy = settings.Privacy with
                {
                    PersonalizationMemoryEnabled = requested,
                },
            });
            LiveModeMemorySnapshot snapshot;
            try
            {
                snapshot = await runtime.LiveModeMemory.SetEnabledAsync(requested);
            }
            catch
            {
                await runtime.UpdateApplicationSettingsAsync(settings => settings with
                {
                    Privacy = settings.Privacy with
                    {
                        PersonalizationMemoryEnabled = previous,
                    },
                });
                throw;
            }
            ApplyMemoryState(snapshot);
        }
        catch (IOException)
        {
            await ShowMessageAsync(
                ResourceText.Get("MemoryStorageFailureTitle"),
                ResourceText.Get("MemoryStorageFailureMessage"));
            await RefreshMemoryStateAsync();
        }
        catch (UnauthorizedAccessException)
        {
            await ShowMessageAsync(
                ResourceText.Get("MemoryStorageFailureTitle"),
                ResourceText.Get("MemoryStorageFailureMessage"));
            await RefreshMemoryStateAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            await ShowMessageAsync(
                ResourceText.Get("MemoryStorageFailureTitle"),
                ResourceText.Get("MemoryStorageFailureMessage"));
            await RefreshMemoryStateAsync();
        }
    }

    private async void OnPrivacySettingChanged(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings || App.CurrentRuntime is not { } runtime)
        {
            return;
        }

        try
        {
            await runtime.UpdateApplicationSettingsAsync(settings => settings with
            {
                Interaction = settings.Interaction with
                {
                    CaptureScope = PrivacyDisplayOption.IsChecked == true
                        ? CaptureScope.FullDisplay
                        : CaptureScope.ActiveWindow,
                },
                Privacy = settings.Privacy with
                {
                    AllowNavigationGuidanceCapture = AllowNavigationGuidanceToggle.IsOn,
                },
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await ShowMessageAsync(
                ResourceText.Get("PrivacySettingsSaveFailedTitle"),
                ResourceText.Get("PrivacySettingsSaveFailedMessage"));
            ApplyPrivacySettings();
        }
    }

    private void ApplyPrivacySettings()
    {
        if (App.CurrentRuntime is not { } runtime)
        {
            return;
        }

        _loadingSettings = true;
        try
        {
            PrivacyActiveWindowOption.IsChecked =
                runtime.CurrentSettings.Interaction.CaptureScope == CaptureScope.ActiveWindow;
            PrivacyDisplayOption.IsChecked =
                runtime.CurrentSettings.Interaction.CaptureScope == CaptureScope.FullDisplay;
            AllowNavigationGuidanceToggle.IsOn =
                runtime.CurrentSettings.Privacy.AllowNavigationGuidanceCapture;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private async void OnViewMemoryClicked(object sender, RoutedEventArgs args)
    {
        if (App.CurrentRuntime is not { } runtime)
        {
            return;
        }

        LiveModeMemorySnapshot snapshot = await runtime.LiveModeMemory.GetAsync();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ResourceText.Get("MemoryDialogTitle"),
            CloseButtonText = ResourceText.Get("DialogCloseText"),
            DefaultButton = ContentDialogButton.Close,
        };

        void Populate(LiveModeMemorySnapshot current)
        {
            var items = new StackPanel { Spacing = 10, MinWidth = 420 };
            if (current.Entries.Count == 0)
            {
                items.Children.Add(new TextBlock
                {
                    Text = ResourceText.Get("MemoryEmptyMessage"),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.72,
                });
            }
            else
            {
                foreach (LiveModeMemoryEntry entry in current.Entries)
                {
                    var row = new Grid { ColumnSpacing = 12 };
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var text = new TextBlock
                    {
                        Text = entry.Text,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 520,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var delete = new Button
                    {
                        Content = ResourceText.Get("MemoryDeleteText"),
                        Tag = entry.Id,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    AutomationProperties.SetName(
                        delete,
                        $"{ResourceText.Get("MemoryDeleteText")}: {entry.Text}");
                    delete.Click += async (_, _) =>
                    {
                        if (delete.Tag is Guid id && await runtime.LiveModeMemory.DeleteAsync(id))
                        {
                            Populate(await runtime.LiveModeMemory.GetAsync());
                            await RefreshMemoryStateAsync();
                        }
                    };
                    Grid.SetColumn(delete, 1);
                    row.Children.Add(text);
                    row.Children.Add(delete);
                    items.Children.Add(row);
                }
            }

            dialog.Content = new ScrollViewer
            {
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = items,
            };
        }

        Populate(snapshot);
        _ = await dialog.ShowAsync();
    }

    private async void OnClearMemoryClicked(object sender, RoutedEventArgs args)
    {
        if (App.CurrentRuntime is not { } runtime)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ResourceText.Get("MemoryClearTitle"),
            Content = ResourceText.Get("MemoryClearMessage"),
            PrimaryButtonText = ResourceText.Get("MemoryClearConfirmText"),
            CloseButtonText = ResourceText.Get("DialogCancelText"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await runtime.LiveModeMemory.ClearAsync();
            await RefreshMemoryStateAsync();
        }
    }

    private async Task RefreshMemoryStateAsync()
    {
        if (App.CurrentRuntime is not { } runtime)
        {
            PersonalizationMemoryToggle.IsEnabled = false;
            ViewMemoryButton.IsEnabled = false;
            ClearMemoryButton.IsEnabled = false;
            return;
        }

        _loadingMemory = true;
        try
        {
            ApplyMemoryState(await runtime.LiveModeMemory.GetAsync());
        }
        finally
        {
            _loadingMemory = false;
        }
    }

    private void ApplyMemoryState(LiveModeMemorySnapshot snapshot)
    {
        PersonalizationMemoryToggle.IsEnabled = true;
        PersonalizationMemoryToggle.IsOn = snapshot.IsEnabled;
        ViewMemoryButton.IsEnabled = true;
        ClearMemoryButton.IsEnabled = snapshot.Entries.Count > 0;
        PrivacyHeader.StatusText = snapshot.IsEnabled
            ? ResourceText.Get("MemoryEnabledStatus")
            : ResourceText.Get("MemoryDisabledStatus");
        PrivacyHeader.StatusDetail = string.Format(
            ResourceText.Get("MemoryEntryCountFormat"),
            snapshot.Entries.Count);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = ResourceText.Get("DialogCloseText"),
        };
        _ = await dialog.ShowAsync();
    }
}
