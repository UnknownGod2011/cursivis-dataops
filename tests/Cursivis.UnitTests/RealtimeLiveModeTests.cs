using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cursivis.Application.OpenAI;
using Cursivis.Application.Realtime;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Models;

namespace Cursivis.UnitTests;

public sealed class RealtimeLiveModeTests
{
    [Fact]
    public void Reducer_BargeInMovesSpeakingSessionBackToUserSpeaking()
    {
        LiveModeSnapshot snapshot = LiveModeReducer.Reduce(
            LiveModeSnapshot.Initial,
            new LiveModeStartRequested());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeConnected());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeAssistantTranscriptDelta("Hello"));

        LiveModeSnapshot interrupted = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStarted());

        Assert.Equal(LiveModeState.UserSpeaking, interrupted.State);
        Assert.Equal("Hello", interrupted.AssistantTranscript);
        Assert.Equal(string.Empty, interrupted.UserTranscript);
    }

    [Fact]
    public void Reducer_RejectsConversationEventsBeforeSessionStarts()
    {
        InvalidLiveModeTransitionException exception = Assert.Throws<InvalidLiveModeTransitionException>(() =>
            LiveModeReducer.Reduce(LiveModeSnapshot.Initial, new LiveModeSpeechStarted()));

        Assert.Equal(LiveModeState.Idle, exception.State);
    }

    [Fact]
    public void Reducer_ToleratesDuplicateAndLateSpeechLifecycleEvents()
    {
        LiveModeSnapshot snapshot = LiveModeReducer.Reduce(
            LiveModeSnapshot.Initial,
            new LiveModeStartRequested());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeConnected());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeConnected());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStarted());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStarted());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStopped());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStopped());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeAssistantAudioReceived());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStopped());

        Assert.Equal(LiveModeState.Speaking, snapshot.State);
    }

    [Fact]
    public void Reducer_IgnoresLateSessionEventsWhileStopping()
    {
        LiveModeSnapshot snapshot = LiveModeReducer.Reduce(
            LiveModeSnapshot.Initial,
            new LiveModeStartRequested());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeConnected());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeStopRequested());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStarted());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeAudioLevelChanged(0.8f));

        Assert.Equal(LiveModeState.Stopping, snapshot.State);
        Assert.Equal(0, snapshot.AudioLevel);
    }

    [Fact]
    public void Reducer_LateFinalTranscriptDoesNotReopenCompletedTurn()
    {
        LiveModeSnapshot snapshot = LiveModeReducer.Reduce(
            LiveModeSnapshot.Initial,
            new LiveModeStartRequested());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeConnected());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStarted());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStopped());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeAssistantTranscriptDelta("Done"));
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeResponseDone());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeUserTranscriptDone("Goodbye"));

        Assert.Equal(LiveModeState.Listening, snapshot.State);
        Assert.Equal("Goodbye", snapshot.UserTranscript);
        Assert.Equal("Done", snapshot.AssistantTranscript);
    }

    [Fact]
    public void Reducer_EmptyVadTurnReturnsToListening()
    {
        LiveModeSnapshot snapshot = LiveModeReducer.Reduce(
            LiveModeSnapshot.Initial,
            new LiveModeStartRequested());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeConnected());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStarted());
        snapshot = LiveModeReducer.Reduce(snapshot, new LiveModeSpeechStopped());

        Assert.Equal(LiveModeState.Listening, snapshot.State);
    }

    [Fact]
    public async Task LiveService_IsTheOnlyPathThatConnectsRealtimeAndStopsCleanly()
    {
        var gateway = new FakeRealtimeGateway();
        gateway.Session.Publish(ServerEvent(RealtimeServerEventKind.Connected));
        var audio = new FakeRealtimeAudioSession();
        await using var service = CreateService(gateway, audio);

        await service.StartAsync();

        Assert.Equal(1, gateway.ConnectCount);
        Assert.Equal(ModelCatalog.Realtime.Value, gateway.Options!.Model);
        Assert.Equal(LiveModeState.Listening, service.Snapshot.State);

        await service.StopAsync();

        Assert.Equal(LiveModeState.Ended, service.Snapshot.State);
        Assert.True(gateway.Session.Disposed);
        Assert.True(audio.Disposed);
    }

    [Fact]
    public async Task LiveService_SendsCapturedSelectionAsBoundedSessionContext()
    {
        var gateway = new FakeRealtimeGateway();
        gateway.Session.Publish(ServerEvent(RealtimeServerEventKind.Connected));
        var audio = new FakeRealtimeAudioSession();
        await using var service = CreateService(gateway, audio);

        await service.StartAsync();

        string context = Assert.IsType<string>(gateway.Options!.ContextInstructions);
        Assert.Contains("Selected context", context, StringComparison.Ordinal);
        Assert.Contains("notepad", context, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("get_selected_text", context, StringComparison.Ordinal);
        await service.StopAsync();
    }

    [Fact]
    public async Task LiveService_ToolCallUsesCapturedContextAndReturnsTypedResult()
    {
        var gateway = new FakeRealtimeGateway();
        gateway.Session.Publish(ServerEvent(RealtimeServerEventKind.Connected));
        var audio = new FakeRealtimeAudioSession();
        await using var service = CreateService(gateway, audio);
        await service.StartAsync();

        gateway.Session.Publish(new RealtimeServerEvent(
            RealtimeServerEventKind.ToolCall,
            null,
            ReadOnlyMemory<byte>.Empty,
            "get_selected_text",
            "call-1",
            "{}",
            null));

        RealtimeClientEvent sent = await gateway.Session.NextSentEvent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RealtimeClientEventKind.ToolResult, sent.Kind);
        Assert.Equal("call-1", sent.CallId);
        Assert.Contains("Selected context", sent.Json, StringComparison.Ordinal);
        await service.StopAsync();
    }

    [Fact]
    public async Task LiveService_SpeechStartClearsQueuedPlaybackForBargeIn()
    {
        var gateway = new FakeRealtimeGateway();
        gateway.Session.Publish(ServerEvent(RealtimeServerEventKind.Connected));
        var audio = new FakeRealtimeAudioSession();
        await using var service = CreateService(gateway, audio);
        await service.StartAsync();
        gateway.Session.Publish(new RealtimeServerEvent(
            RealtimeServerEventKind.AssistantAudioDelta,
            null,
            new byte[] { 0, 0 },
            null,
            null,
            null,
            null));
        await audio.PlaybackQueued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gateway.Session.Publish(ServerEvent(RealtimeServerEventKind.ResponseDone));
        await WaitUntilAsync(
            () => service.Snapshot.State == LiveModeState.Listening,
            TimeSpan.FromSeconds(2));

        gateway.Session.Publish(ServerEvent(RealtimeServerEventKind.SpeechStarted));
        await audio.PlaybackCleared.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(LiveModeState.UserSpeaking, service.Snapshot.State);
        await service.StopAsync();
    }

    [Fact]
    public async Task LiveService_FiveMinutesOfSyntheticPcm_ForwardsEveryFrameOnce()
    {
        var gateway = new FakeRealtimeGateway();
        gateway.Session.Publish(ServerEvent(RealtimeServerEventKind.Connected));
        var audio = new FakeRealtimeAudioSession();
        await using var service = CreateService(gateway, audio);
        await service.StartAsync();

        byte[] oneHundredMillisecondsPcm = new byte[4_800];
        const int fiveMinuteFrameCount = 3_000;
        for (int index = 0; index < fiveMinuteFrameCount; index++)
        {
            audio.Publish(new RealtimeAudioFrame(oneHundredMillisecondsPcm, 0.1f));
        }

        await WaitUntilAsync(
            () => gateway.Session.SentEvents.Count(static item =>
                item.Kind == RealtimeClientEventKind.AudioChunk) == fiveMinuteFrameCount,
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, gateway.ConnectCount);
        Assert.Equal(
            fiveMinuteFrameCount,
            gateway.Session.SentEvents.Count(static item =>
                item.Kind == RealtimeClientEventKind.AudioChunk));
        await service.StopAsync();
    }

    [Fact]
    public async Task LiveService_UnexpectedMicrophoneCompletion_ReportsErrorInsteadOfSilentlyEnding()
    {
        var gateway = new FakeRealtimeGateway();
        gateway.Session.Publish(ServerEvent(RealtimeServerEventKind.Connected));
        var audio = new FakeRealtimeAudioSession();
        await using var service = CreateService(gateway, audio);
        await service.StartAsync();

        audio.Complete();
        await WaitUntilAsync(
            () => service.Snapshot.State == LiveModeState.Error,
            TimeSpan.FromSeconds(2));

        Assert.Contains("microphone stream ended", service.Snapshot.SafeError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolExecutor_RejectsArgumentsAndUnknownTools()
    {
        var executor = new LiveModeToolExecutor();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(LiveModeContext.Empty, "get_selected_text", "{\"extra\":true}"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(LiveModeContext.Empty, "not_allowlisted", "{}"));
    }

    private static RealtimeLiveModeService CreateService(
        FakeRealtimeGateway gateway,
        FakeRealtimeAudioSession audio) =>
        new(
            gateway,
            new FakeRealtimeAudioSessionFactory(audio),
            new FakeLiveModeContextProvider(new LiveModeContext(
                "Selected context",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "notepad",
                "Draft")),
            new LiveModeToolExecutor(),
            ModelCatalog.Realtime.Value);

    private static RealtimeServerEvent ServerEvent(RealtimeServerEventKind kind) =>
        new(kind, null, ReadOnlyMemory<byte>.Empty, null, null, null, null);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected asynchronous condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeRealtimeGateway : IRealtimeGateway
    {
        public FakeRealtimeSession Session { get; } = new();

        public int ConnectCount { get; private set; }

        public RealtimeSessionOptions? Options { get; private set; }

        public Task<IRealtimeSession> ConnectAsync(
            RealtimeSessionOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            Options = options;
            return Task.FromResult<IRealtimeSession>(Session);
        }
    }

    private sealed class FakeRealtimeSession : IRealtimeSession
    {
        private readonly Channel<RealtimeServerEvent> _events = Channel.CreateUnbounded<RealtimeServerEvent>();

        public ConcurrentQueue<RealtimeClientEvent> SentEvents { get; } = new();

        public TaskCompletionSource<RealtimeClientEvent> NextSentEvent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public void Publish(RealtimeServerEvent serverEvent) => _events.Writer.TryWrite(serverEvent);

        public async IAsyncEnumerable<RealtimeServerEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (RealtimeServerEvent serverEvent in _events.Reader
                               .ReadAllAsync(cancellationToken))
            {
                yield return serverEvent;
            }
        }

        public ValueTask SendAsync(
            RealtimeClientEvent clientEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentEvents.Enqueue(clientEvent);
            NextSentEvent.TrySetResult(clientEvent);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRealtimeAudioSessionFactory(FakeRealtimeAudioSession session)
        : IRealtimeAudioSessionFactory
    {
        public Task<IRealtimeAudioSession> OpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IRealtimeAudioSession>(session);
        }
    }

    private sealed class FakeRealtimeAudioSession : IRealtimeAudioSession
    {
        private readonly Channel<RealtimeAudioFrame> _frames = Channel.CreateUnbounded<RealtimeAudioFrame>();

        public TaskCompletionSource PlaybackQueued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PlaybackCleared { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public void Publish(RealtimeAudioFrame frame) => _frames.Writer.TryWrite(frame);

        public void Complete() => _frames.Writer.TryComplete();

        public async IAsyncEnumerable<RealtimeAudioFrame> ReadCapturedAudioAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (RealtimeAudioFrame frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public ValueTask QueuePlaybackAsync(
            ReadOnlyMemory<byte> pcm16Mono24Khz,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlaybackQueued.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearPlaybackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlaybackCleared.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLiveModeContextProvider(LiveModeContext context) : ILiveModeContextProvider
    {
        public Task<LiveModeContext> CaptureAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(context);
        }
    }
}
