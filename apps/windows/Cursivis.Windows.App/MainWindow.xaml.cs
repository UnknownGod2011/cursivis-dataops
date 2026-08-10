using Cursivis.Windows.App.Helpers;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Cursivis.Windows.App;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();

        Title = ResourceText.Get("WindowTitle");
        AppWindow.SetIcon("Assets/AppIcon.ico");
        NativeWindowPositioner.FitAndCenter(AppWindow, 1180, 800);
        RootFrame.Content = new MainPage();
        // Closing Settings must not tear down the resident process. Hotkeys,
        // Live Mode, and overlays stay registered while Cursivis runs in the
        // background; only an explicit quit path should dispose the runtime.
        AppWindow.Closing += OnAppWindowClosing;
    }

    internal void RefreshRuntimeStatus() =>
        (RootFrame.Content as MainPage)?.RefreshRuntimeStatus();

    internal void ShowForActivation() => Activate();

    internal void HideForBackgroundStartup() => AppWindow.Hide();

    internal void RequestQuit()
    {
        _allowClose = true;
        Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
    }
}
