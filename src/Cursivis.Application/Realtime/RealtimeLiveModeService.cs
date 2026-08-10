using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;

namespace Cursivis.Application.Realtime;

public sealed class RealtimeLiveModeService : IRealtimeLiveModeService
{
    private const string DefaultVoice = "marin";
    private readonly IRealtimeGateway _gateway;
    private readonly IRealtimeAudioSessionFactory _audioFactory;
    private readonly ILiveModeContextProvider _contextProvider;
    private readonly ILiveModeToolExecutor _toolExecutor;
    private readonly RealtimeSessionOptions _sessionOptions;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private LiveModeSnapshot _snapshot = LiveModeSnapshot.Initial;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private Guid _activeRunId;
    private bool _disposed;

    public RealtimeLiveModeService(
        IRealtimeGateway gateway,
        IRealtimeAudioSessionFactory audioFactory,
        ILiveModeContextProvider contextProvider,
        ILiveModeToolExecutor toolExecutor,
        string model,
        string voice = DefaultVoice,
        string? language = null,
        bool semanticVad = true)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _audioFactory = audioFactory ?? throw new ArgumentNullException(nameof(audioFactory));
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(voice);
        _sessionOptions = new RealtimeSessionOptions(
            model.Trim(),
            voice.Trim(),
            string.IsNullOrWhiteSpace(language) ? null : language.Trim(),
            semanticVad,
            toolExecutor.Definitions);
    }

    public LiveModeSnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
            {
                return _snapshot;
            }
        }
    }

    public event Action<LiveModeSnapshot>? SnapshotChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runTask is { IsCompleted: false })
            {
                return;
            }

            Guid runId = Guid.NewGuid();
            CancellationTokenSource runCancellation = new();
            _activeRunId = runId;
            _runCancellation?.Dispose();
            _runCancellation = runCancellation;
            Publish(runId, new LiveModeStartRequested());
            _runTask = RunAsync(runId, runCancellation, connected);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? runCancellation;
        Task? runTask;
        Guid runId;

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            runTask = _runTask;
            runCancellation = _runCancellation;
            runId = _activeRunId;
            if (runTask is null || runTask.IsCompleted)
            {
                return;
            }

            LiveModeSnapshot current = Snapshot;
            if (current.IsActive && current.State != LiveModeState.Stopping)
            {
                Publish(runId, new LiveModeStopRequested());
            }

            runCancellation?.Cancel();
        }
        finally
        {
            _lifecycleGate.Release();
        }

        try
        {
            await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The session's own cancellation is the expected stop path.
        }
    }

    public Task ToggleAsync(CancellationToken cancellationToken = default) =>
        Snapshot.IsActive
            ? StopAsync(cancellationToken)
            : StartAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        _runCancellation?.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task RunAsync(
        Guid runId,
        CancellationTokenSource runCancellation,
        TaskCompletionSource connected)
    {
        CancellationToken cancellationToken = runCancellation.Token;
        IRealtimeSession? session = null;
        IRealtimeAudioSession? audio = null;
        try
        {
            LiveModeContext context = await _contextProvider.CaptureAsync(cancellationToken)
                .ConfigureAwait(false);
            RealtimeSessionOptions sessionOptions = _sessionOptions with
            {
                ContextInstructions = BuildContextInstructions(context),
            };
            session = await _gateway.ConnectAsync(sessionOptions, cancellationToken)
                .ConfigureAwait(false);
            audio = await _audioFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

            Task captureTask = SendCapturedAudioAsync(runId, audio, session, cancellationToken);
            Task receiveTask = ReceiveEventsAsync(
                runId,
                context,
                audio,
                session,
                connected,
                runCancellation,
                cancellationToken);

            Task completed = await Task.WhenAny(captureTask, receiveTask).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw completed == captureTask
                    ? new LiveModeAudioStreamEndedException()
                    : new LiveModeTransportEndedException();
            }

            runCancellation.Cancel();
            await IgnoreSessionCancellationAsync(captureTask).ConfigureAwait(false);
            await IgnoreSessionCancellationAsync(receiveTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            connected.TrySetResult();
        }
        catch (RealtimeGatewayException exception)
        {
            Publish(runId, new LiveModeFailed(exception.Failure.SafeMessage));
            connected.TrySetResult();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or IOException
                                          or System.ComponentModel.Win32Exception)
        {
            Publish(runId, new LiveModeFailed(ToSafeFailure(exception)));
            connected.TrySetResult();
        }
        catch (Exception)
        {
            Publish(runId, new LiveModeFailed("Live Mode ended because of an unexpected session failure."));
            connected.TrySetResult();
        }
        finally
        {
            if (audio is not null)
            {
                await audio.DisposeAsync().ConfigureAwait(false);
            }

            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            LiveModeSnapshot current = Snapshot;
            if (_activeRunId == runId && current.State != LiveModeState.Error && current.State != LiveModeState.Ended)
            {
                Publish(runId, new LiveModeStopped());
            }

            connected.TrySetResult();
        }
    }

    private async Task SendCapturedAudioAsync(
        Guid runId,
        IRealtimeAudioSession audio,
        IRealtimeSession session,
        CancellationToken cancellationToken)
    {
        await foreach (RealtimeAudioFrame frame in audio.ReadCapturedAudioAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            Publish(runId, new LiveModeAudioLevelChanged(frame.Level));
            if (frame.Pcm16Mono24Khz.IsEmpty)
            {
                continue;
            }

            await session.SendAsync(new RealtimeClientEvent(
                RealtimeClientEventKind.AudioChunk,
                frame.Pcm16Mono24Khz,
                null,
                null), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveEventsAsync(
        Guid runId,
        LiveModeContext context,
        IRealtimeAudioSession audio,
        IRealtimeSession session,
        TaskCompletionSource connected,
        CancellationTokenSource runCancellation,
        CancellationToken cancellationToken)
    {
        await foreach (RealtimeServerEvent serverEvent in session.ReadEventsAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            switch (serverEvent.Kind)
            {
                case RealtimeServerEventKind.Connected:
                    if (Snapshot.State == LiveModeState.Connecting)
                    {
                        Publish(runId, new LiveModeConnected());
                    }

                    connected.TrySetResult();
                    break;
                case RealtimeServerEventKind.SpeechStarted:
                    // The speaker can still contain buffered audio after the
                    // state reducer has returned to Listening. Always clear it
                    // when VAD detects barge-in so stale speech cannot leak into
                    // the next user turn.
                    await audio.ClearPlaybackAsync(cancellationToken).ConfigureAwait(false);

                    Publish(runId, new LiveModeSpeechStarted());
                    break;
                case RealtimeServerEventKind.SpeechStopped:
                    Publish(runId, new LiveModeSpeechStopped());
                    break;
                case RealtimeServerEventKind.UserTranscriptDelta:
                    if (!string.IsNullOrEmpty(serverEvent.Text))
                    {
                        Publish(runId, new LiveModeUserTranscriptDelta(serverEvent.Text));
                    }

                    break;
                case RealtimeServerEventKind.UserTranscriptDone:
                    if (!string.IsNullOrWhiteSpace(serverEvent.Text))
                    {
                        Publish(runId, new LiveModeUserTranscriptDone(serverEvent.Text));
                    }

                    break;
                case RealtimeServerEventKind.AssistantTranscriptDelta:
                    if (!string.IsNullOrEmpty(serverEvent.Text))
                    {
                        Publish(runId, new LiveModeAssistantTranscriptDelta(serverEvent.Text));
                    }

                    break;
                case RealtimeServerEventKind.AssistantAudioDelta:
                    if (!serverEvent.Audio.IsEmpty)
                    {
                        Publish(runId, new LiveModeAssistantAudioReceived());
                        await audio.QueuePlaybackAsync(serverEvent.Audio, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;
                case RealtimeServerEventKind.ToolCall:
                    await ExecuteToolAsync(
                        runId,
                        context,
                        serverEvent,
                        session,
                        runCancellation,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case RealtimeServerEventKind.ResponseDone:
                    Publish(runId, new LiveModeResponseDone());
                    break;
                case RealtimeServerEventKind.Error:
                    throw new RealtimeGatewayException(serverEvent.Failure ?? new OpenAiFailure(
                        OpenAiFailureKind.Unknown,
                        "OpenAI Realtime reported a session error.",
                        false));
                case RealtimeServerEventKind.Closed:
                    return;
                default:
                    throw new InvalidOperationException("OpenAI Realtime returned an unsupported event.");
            }
        }
    }

    private async Task ExecuteToolAsync(
        Guid runId,
        LiveModeContext context,
        RealtimeServerEvent serverEvent,
        IRealtimeSession session,
        CancellationTokenSource runCancellation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverEvent.ToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverEvent.CallId);
        Publish(runId, new LiveModeToolStarted(serverEvent.ToolName));
        LiveModeToolExecutionResult result = await _toolExecutor.ExecuteAsync(
            context,
            serverEvent.ToolName,
            serverEvent.UntrustedArgumentsJson ?? "{}",
            cancellationToken).ConfigureAwait(false);

        if (result.StopSession)
        {
            runCancellation.Cancel();
            return;
        }

        await session.SendAsync(new RealtimeClientEvent(
            RealtimeClientEventKind.ToolResult,
            ReadOnlyMemory<byte>.Empty,
            serverEvent.CallId,
            result.Json), cancellationToken).ConfigureAwait(false);
        Publish(runId, new LiveModeToolFinished());
    }

    private void Publish(Guid runId, LiveModeEvent @event)
    {
        LiveModeSnapshot next;
        lock (_stateGate)
        {
            if (runId != _activeRunId)
            {
                return;
            }

            next = LiveModeReducer.Reduce(_snapshot, @event);
            _snapshot = next;
        }

        SnapshotChanged?.Invoke(next);
    }

    private static string? BuildContextInstructions(LiveModeContext context)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(context.ActiveApplication))
        {
            string title = string.IsNullOrWhiteSpace(context.ActiveWindowTitle)
                ? string.Empty
                : $" ({context.ActiveWindowTitle.Trim()})";
            parts.Add($"Active application: {context.ActiveApplication.Trim()}{title}.");
        }

        if (!string.IsNullOrWhiteSpace(context.SelectedText))
        {
            string text = context.SelectedText.Trim();
            if (text.Length > 4_000)
            {
                text = text[..4_000] + "…";
            }

            parts.Add(
                "The user had this text selected when Live Mode started. " +
                "Use get_selected_text if you need a refreshed capture:\n" +
                text);
        }
        else
        {
            parts.Add(
                "No selected text was captured at Live Mode start. " +
                "Call get_selected_text after the user selects text, or " +
                "analyze_screen_region when they ask to look at the screen.");
        }

        return parts.Count == 0 ? null : string.Join('\n', parts);
    }

    private static async Task IgnoreSessionCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string ToSafeFailure(Exception exception) => exception switch
    {
        System.ComponentModel.Win32Exception =>
            "Cursivis could not open the configured microphone or speaker.",
        InvalidLiveModeTransitionException =>
            "OpenAI Realtime returned an unexpected event sequence. Try Live Mode again.",
        LiveModeAudioStreamEndedException =>
            "The microphone stream ended unexpectedly. Check the selected device and try Live Mode again.",
        LiveModeTransportEndedException =>
            "The OpenAI Realtime session closed unexpectedly. Check the connection and try again.",
        IOException => "The Live Mode audio session ended unexpectedly.",
        _ => "Live Mode could not complete the conversation session.",
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class LiveModeAudioStreamEndedException : IOException;

    private sealed class LiveModeTransportEndedException : IOException;
}
