using System.Collections.Concurrent;
using Cursivis.Windows.Platform.Hotkeys;

namespace Cursivis.IntegrationTests.Windows;

public sealed class WindowThreadNativeHotkeyApiTests
{
    private static readonly HotkeyChord Chord = new(
        HotkeyModifiers.Control | HotkeyModifiers.Alt,
        0x4F);

    [Fact]
    public async Task Register_FromWorker_ExecutesOnTheOwningWindowThread()
    {
        using var dispatcher = new DedicatedWindowThreadDispatcher();
        var native = new RecordingNativeHotkeyApi();
        var adapter = new WindowThreadNativeHotkeyApi(dispatcher, native);

        Task<NativeHotkeyOperationResult> pending = Task.Run(
            () => adapter.Register((nint)123, 7, Chord));
        Assert.True((await pending).Succeeded);
        Assert.Equal(dispatcher.OwnerThreadId, native.RegisterThreadId);
    }

    [Fact]
    public async Task Unregister_FromWorker_ExecutesOnTheOwningWindowThread()
    {
        using var dispatcher = new DedicatedWindowThreadDispatcher();
        var native = new RecordingNativeHotkeyApi();
        var adapter = new WindowThreadNativeHotkeyApi(dispatcher, native);

        Task<NativeHotkeyOperationResult> pending = Task.Run(
            () => adapter.Unregister((nint)123, 7));
        Assert.True((await pending).Succeeded);
        Assert.Equal(dispatcher.OwnerThreadId, native.UnregisterThreadId);
    }

    [Fact]
    public void Register_WhenDispatcherIsUnavailable_DoesNotTouchNativeApi()
    {
        var native = new RecordingNativeHotkeyApi();
        var adapter = new WindowThreadNativeHotkeyApi(
            new UnavailableWindowThreadDispatcher(),
            native);

        var exception = Assert.Throws<InvalidOperationException>(
            () => adapter.Register((nint)123, 7, Chord));

        Assert.Contains("dispatcher", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(native.RegisterThreadId);
    }

    private sealed class DedicatedWindowThreadDispatcher : IWindowThreadDispatcher, IDisposable
    {
        private readonly BlockingCollection<Action> _work = [];
        private readonly Thread _thread;
        private readonly TaskCompletionSource<int> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DedicatedWindowThreadDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Cursivis Window Thread Test",
            };
            _thread.Start();
            OwnerThreadId = _ready.Task.GetAwaiter().GetResult();
        }

        public int OwnerThreadId { get; }

        public bool HasThreadAccess => Environment.CurrentManagedThreadId == OwnerThreadId;

        public bool TryEnqueue(Action action)
        {
            try
            {
                _work.Add(action);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public Task<T> InvokeAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            if (HasThreadAccess)
            {
                return operation();
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryEnqueue(() =>
                {
                    try
                    {
                        completion.TrySetResult(operation().GetAwaiter().GetResult());
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                }))
            {
                return Task.FromException<T>(new InvalidOperationException("Dispatcher unavailable."));
            }

            return completion.Task;
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)));
            _work.Dispose();
        }

        private void Run()
        {
            _ready.TrySetResult(Environment.CurrentManagedThreadId);
            foreach (Action action in _work.GetConsumingEnumerable())
            {
                action();
            }
        }
    }

    private sealed class UnavailableWindowThreadDispatcher : IWindowThreadDispatcher
    {
        public bool HasThreadAccess => false;

        public bool TryEnqueue(Action action) => false;

        public Task<T> InvokeAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new InvalidOperationException("Dispatcher unavailable."));
    }

    private sealed class RecordingNativeHotkeyApi : INativeHotkeyApi
    {
        public int? RegisterThreadId { get; private set; }

        public int? UnregisterThreadId { get; private set; }

        public NativeHotkeyOperationResult Register(
            nint windowHandle,
            int registrationId,
            HotkeyChord chord)
        {
            RegisterThreadId = Environment.CurrentManagedThreadId;
            return NativeHotkeyOperationResult.Success;
        }

        public NativeHotkeyOperationResult RegisterUnmodified(
            nint windowHandle,
            int registrationId,
            uint virtualKey) => NativeHotkeyOperationResult.Success;

        public NativeHotkeyOperationResult Unregister(nint windowHandle, int registrationId)
        {
            UnregisterThreadId = Environment.CurrentManagedThreadId;
            return NativeHotkeyOperationResult.Success;
        }
    }
}
