using Cursivis.Contracts.Browser;
using Cursivis.Infrastructure.BrowserBridge;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Pages;

public sealed partial class BrowserIntegrationPage : Page
{
    public BrowserIntegrationPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => RefreshStatus();

    private async void OnTestClicked(object sender, RoutedEventArgs args)
    {
        AppRuntime? runtime = App.CurrentRuntime;
        if (runtime is null || !runtime.BrowserBridgeStatus.IsConnected)
        {
            ActionStatus.Text = "Connect the Cursivis browser extension, then try again.";
            RefreshStatus();
            return;
        }

        TestButton.IsEnabled = false;
        ActionStatus.Text = "Checking the visible form in the active tab…";
        try
        {
            BrowserFormSnapshot form = await runtime.TestBrowserIntegrationAsync();
            ActionStatus.Text = $"Connected · found {form.Fields.Count} supported visible {(form.Fields.Count == 1 ? "field" : "fields")}.";
        }
        catch (BrowserBridgeException exception)
        {
            ActionStatus.Text = exception.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
            RefreshStatus(preserveActionStatus: true);
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        RefreshStatus();
        ActionStatus.Text = App.CurrentRuntime?.BrowserBridgeStatus.IsConnected == true
            ? "The authenticated browser connection is ready."
            : "Open the extension options and choose Test connection.";
    }

    private void RefreshStatus(bool preserveActionStatus = false)
    {
        BrowserBridgeSnapshot snapshot = App.CurrentRuntime?.BrowserBridgeStatus ?? BrowserBridgeSnapshot.Stopped;
        bool connected = snapshot.IsConnected;
        string glyph = connected ? "\uE73E" : "\uE711";
        ExtensionStatus.Glyph = glyph;
        NativeHostStatus.Glyph = glyph;
        PipeStatus.Glyph = glyph;
        HandshakeStatus.Glyph = connected ? "\uE73E" : "\uE946";
        ExtensionStatus.StatusText = connected ? "Browser extension · Connected" : "Browser extension · Not connected";
        ExtensionStatus.DetailText = connected && snapshot.ExtensionVersion is not null
            ? $"Version {snapshot.ExtensionVersion}"
            : "Test the connection from the extension options page";
        NativeHostStatus.StatusText = connected ? "Native host · Ready" : "Native host · Waiting";
        NativeHostStatus.DetailText = connected ? "Registered per-user desktop bridge" : "No authenticated extension session";
        PipeStatus.StatusText = connected ? "Named pipe · Connected" : "Named pipe · Waiting";
        PipeStatus.DetailText = snapshot.SafeStatus;
        HandshakeStatus.StatusText = connected ? "Handshake · Authenticated" : "Handshake · Unverified";
        HandshakeStatus.DetailText = snapshot.LastAuthenticatedHandshake is DateTimeOffset handshake
            ? $"Last connected {handshake.ToLocalTime():g}"
            : "No successful handshake in this app session";
        TestButton.IsEnabled = connected;
        if (!preserveActionStatus)
        {
            ActionStatus.Text = connected
                ? "Test reads only the visible form structure in the active permitted tab."
                : "Grant the active site from the extension before testing.";
        }
    }
}
