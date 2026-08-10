using System;
using System.IO;
using Cursivis.Windows.Platform.Instance;
using Microsoft.UI.Dispatching;

using Microsoft.UI.Xaml;

namespace Cursivis.Windows.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;
    private AppRuntime? _runtime;
    private ISingleInstanceLease? _instanceLease;
    private CurrentUserActivationHandoff? _activationHandoff;
    private CancellationTokenSource? _activationLifetime;
    private Task? _activationListener;

    public static AppRuntime? CurrentRuntime => (Microsoft.UI.Xaml.Application.Current as App)?._runtime;

    internal static void ShowSettingsWindow()
    {
        if (Microsoft.UI.Xaml.Application.Current is not App app ||
            app._window is not MainWindow mainWindow)
        {
            return;
        }

        _ = mainWindow.DispatcherQueue.TryEnqueue(mainWindow.ShowForActivation);
    }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var coordinator = new WindowsSingleInstanceCoordinator("Cursivis.Next");
            _instanceLease = coordinator.Acquire();
            _activationHandoff = new CurrentUserActivationHandoff(_instanceLease.Names.ActivationPipeName);
            if (!_instanceLease.IsPrimaryInstance)
            {
                _ = await _activationHandoff.SendAsync(
                    ActivationRequest.Create(ActivationRequestKind.OpenSettings),
                    TimeSpan.FromSeconds(5));
                _instanceLease.Dispose();
                _instanceLease = null;
                Exit();
                return;
            }

            var mainWindow = new MainWindow();
            _window = mainWindow;
            _window.Closed += OnMainWindowClosed;
            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            AppRuntime runtime = await AppRuntime.CreateAsync(windowHandle);
            await EnqueueAsync(mainWindow.DispatcherQueue, () =>
            {
                _runtime = runtime;
                mainWindow.RefreshRuntimeStatus();

                bool backgroundStartup = Environment.GetCommandLineArgs().Any(
                    argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
                if (backgroundStartup && runtime.CredentialManager.HasSavedKey)
                {
                    mainWindow.HideForBackgroundStartup();
                }
                else
                {
                    mainWindow.ShowForActivation();
                }
            });

            _activationLifetime = new CancellationTokenSource();
            _activationListener = _activationHandoff.RunAsync(
                OnActivationRequestedAsync,
                _activationLifetime.Token);
        }
        catch (Exception exception)
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "startup-error.txt"),
                exception.ToString());
            throw;
        }
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        _activationLifetime?.Cancel();
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }

        if (_activationListener is not null)
        {
            try
            {
                await _activationListener;
            }
            catch (OperationCanceledException)
            {
            }
            _activationListener = null;
        }

        _activationLifetime?.Dispose();
        _activationLifetime = null;
        _instanceLease?.Dispose();
        _instanceLease = null;
        Exit();
    }

    private ValueTask OnActivationRequestedAsync(
        ActivationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_window is not MainWindow mainWindow ||
            !mainWindow.DispatcherQueue.TryEnqueue(mainWindow.ShowForActivation))
        {
            throw new InvalidOperationException("The Settings window is unavailable.");
        }

        return ValueTask.CompletedTask;
    }

    private static Task EnqueueAsync(DispatcherQueue dispatcher, Action action)
    {
        if (dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The Settings dispatcher is unavailable."));
        }

        return completion.Task;
    }
}
