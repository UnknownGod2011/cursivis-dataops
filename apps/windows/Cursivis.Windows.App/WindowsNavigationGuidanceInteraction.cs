using Cursivis.Application.Presentation;
using Cursivis.Application.Realtime;
using Cursivis.Windows.Platform.Overlays;
using Microsoft.UI.Dispatching;

namespace Cursivis.Windows.App;

internal sealed class WindowsNavigationGuidanceInteraction : INavigationGuidanceInteraction, IDisposable
{
    private readonly ContextOrbWindow _orb;
    private readonly NativePointerMonitor _pointerMonitor = new();
    private readonly object _gate = new();
    private TaskCompletionSource? _pendingAction;
    private bool _disposed;

    public WindowsNavigationGuidanceInteraction(ContextOrbWindow orb)
    {
        _orb = orb ?? throw new ArgumentNullException(nameof(orb));
        _pointerMonitor.PointerPressed += OnPointerPressed;
    }

    public void Show(NavigationGuidanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = _orb.DispatcherQueue.TryEnqueue(() =>
        {
            (OrbPresentationState state, string status, string detail) = snapshot.State switch
            {
                NavigationGuidanceState.Observing =>
                    (OrbPresentationState.Guiding, "Observing screen", snapshot.Detail),
                NavigationGuidanceState.Analyzing =>
                    (OrbPresentationState.Thinking, "Analyzing screen", snapshot.Detail),
                NavigationGuidanceState.WaitingForUser =>
                    (OrbPresentationState.Guiding, $"Step {snapshot.StepNumber}", $"{snapshot.Status}  Expected: {snapshot.Detail}"),
                NavigationGuidanceState.Verifying =>
                    (OrbPresentationState.Thinking, "Verifying change", snapshot.Detail),
                NavigationGuidanceState.Completed =>
                    (OrbPresentationState.Done, snapshot.Status, snapshot.Detail),
                NavigationGuidanceState.Cancelled =>
                    (OrbPresentationState.Cancelled, snapshot.Status, snapshot.Detail),
                NavigationGuidanceState.Error =>
                    (OrbPresentationState.Error, snapshot.Status, snapshot.Detail),
                _ => (OrbPresentationState.Idle, snapshot.Status, snapshot.Detail),
            };
            _orb.ShowDictationState(state, status, detail);
        });
    }

    public async Task WaitForUserActionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pendingAction is not null)
            {
                throw new InvalidOperationException("Navigation Guidance is already waiting for a user action.");
            }

            _pendingAction = completion;
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try
        {
            await EnqueueAsync(_orb.DispatcherQueue, _pointerMonitor.Start).ConfigureAwait(false);
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingAction, completion))
                {
                    _pendingAction = null;
                }
            }
            await EnqueueAsync(_orb.DispatcherQueue, _pointerMonitor.Stop).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _pendingAction?.TrySetCanceled();
            _pendingAction = null;
        }
        _pointerMonitor.PointerPressed -= OnPointerPressed;
        _pointerMonitor.Dispose();
    }

    private void OnPointerPressed(object? sender, NativePointerPressedEventArgs args)
    {
        if (_orb.IsVisible && Contains(_orb.OverlayBounds, args.Position))
        {
            return;
        }

        TaskCompletionSource? completion;
        lock (_gate)
        {
            completion = _pendingAction;
        }
        completion?.TrySetResult();
    }

    private static bool Contains(OverlayRectangle rectangle, OverlayPoint point) =>
        point.X >= rectangle.X && point.X < rectangle.Right &&
        point.Y >= rectangle.Y && point.Y < rectangle.Bottom;

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
            completion.TrySetException(new InvalidOperationException("The overlay dispatcher is unavailable."));
        }

        return completion.Task;
    }
}
