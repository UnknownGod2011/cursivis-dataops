using Cursivis.Windows.Platform.Hotkeys;
using Microsoft.UI.Dispatching;

namespace Cursivis.Windows.App.Helpers;

/// <summary>
/// Adapts the WinUI dispatcher to the platform-level native hotkey thread
/// adapter without leaking WinUI types into the platform project.
/// </summary>
internal sealed class DispatcherQueueWindowThreadDispatcher : IWindowThreadDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherQueueWindowThreadDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    public bool HasThreadAccess => _dispatcherQueue.HasThreadAccess;

    public bool TryEnqueue(Action action) => _dispatcherQueue.TryEnqueue(() => action());

    public Task<T> InvokeAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (HasThreadAccess)
        {
            return operation();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(await operation().ConfigureAwait(true));
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            return Task.FromException<T>(
                new InvalidOperationException("The Cursivis UI dispatcher is unavailable."));
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(
                static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(),
                completion);
        }

        return completion.Task;
    }
}
