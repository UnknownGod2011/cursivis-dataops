namespace Cursivis.Application.Realtime;

public abstract record LiveModeEvent;

public sealed record LiveModeStartRequested : LiveModeEvent;
public sealed record LiveModeConnected : LiveModeEvent;
public sealed record LiveModeSpeechStarted : LiveModeEvent;
public sealed record LiveModeSpeechStopped : LiveModeEvent;
public sealed record LiveModeUserTranscriptDelta(string Delta) : LiveModeEvent;
public sealed record LiveModeUserTranscriptDone(string Transcript) : LiveModeEvent;
public sealed record LiveModeAssistantTranscriptDelta(string Delta) : LiveModeEvent;
public sealed record LiveModeAssistantAudioReceived : LiveModeEvent;
public sealed record LiveModeResponseDone : LiveModeEvent;
public sealed record LiveModeToolStarted(string ToolName) : LiveModeEvent;
public sealed record LiveModeToolFinished : LiveModeEvent;
public sealed record LiveModeAudioLevelChanged(float Level) : LiveModeEvent;
public sealed record LiveModeStopRequested : LiveModeEvent;
public sealed record LiveModeStopped : LiveModeEvent;
public sealed record LiveModeFailed(string SafeMessage) : LiveModeEvent;

public sealed class InvalidLiveModeTransitionException(LiveModeState state, Type eventType)
    : InvalidOperationException($"Event '{eventType.Name}' is not valid while Live Mode is '{state}'.")
{
    public LiveModeState State { get; } = state;

    public Type EventType { get; } = eventType;
}

/// <summary>
/// Pure, authoritative reducer for a Live Mode session. Transport, audio, and UI
/// can only change visible state by dispatching one of these typed events.
/// </summary>
public static class LiveModeReducer
{
    public static LiveModeSnapshot Reduce(LiveModeSnapshot current, LiveModeEvent @event)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(@event);

        return @event switch
        {
            LiveModeStartRequested when IsTerminal(current.State) => current with
            {
                State = LiveModeState.Connecting,
                UserTranscript = string.Empty,
                AssistantTranscript = string.Empty,
                AudioLevel = 0,
                Status = "Connecting",
                SafeError = null,
            },
            LiveModeConnected when current.State == LiveModeState.Connecting => current with
            {
                State = LiveModeState.Listening,
                Status = "Listening",
            },
            // Realtime can acknowledge an updated session more than once.
            // Once conversation processing has started, that update is
            // idempotent and must not tear down an otherwise healthy session.
            LiveModeConnected when CanReceiveConversationEvents(current.State) => current,
            LiveModeSpeechStarted when current.State == LiveModeState.UserSpeaking => current,
            LiveModeSpeechStarted when CanReceiveConversationEvents(current.State) => current with
            {
                State = LiveModeState.UserSpeaking,
                UserTranscript = string.Empty,
                Status = "Listening",
            },
            LiveModeSpeechStopped when current.State == LiveModeState.UserSpeaking => current with
            {
                State = string.IsNullOrWhiteSpace(current.UserTranscript)
                    ? LiveModeState.Listening
                    : LiveModeState.Thinking,
                AssistantTranscript = string.IsNullOrWhiteSpace(current.UserTranscript)
                    ? current.AssistantTranscript
                    : string.Empty,
                Status = string.IsNullOrWhiteSpace(current.UserTranscript)
                    ? "Listening"
                    : "Thinking",
            },
            // Speech-stop and transcription events are delivered by separate
            // server pipelines. A duplicate or a late stop may arrive after
            // the reducer has already advanced to Thinking or Speaking.
            LiveModeSpeechStopped when CanReceiveConversationEvents(current.State) => current,
            LiveModeUserTranscriptDelta delta when CanReceiveConversationEvents(current.State) => current with
            {
                State = current.State == LiveModeState.Speaking
                    ? LiveModeState.Speaking
                    : LiveModeState.UserSpeaking,
                UserTranscript = Append(current.UserTranscript, delta.Delta),
                Status = current.State == LiveModeState.Speaking ? "Speaking" : "Listening",
            },
            LiveModeUserTranscriptDone done when CanReceiveConversationEvents(current.State) => current with
            {
                UserTranscript = RequireText(done.Transcript),
                State = current.State == LiveModeState.Speaking
                    ? LiveModeState.Speaking
                    : current.State == LiveModeState.Listening
                        ? LiveModeState.Listening
                        : LiveModeState.Thinking,
                Status = current.State == LiveModeState.Speaking
                    ? "Speaking"
                    : current.State == LiveModeState.Listening
                        ? "Listening"
                        : "Thinking",
            },
            LiveModeAssistantTranscriptDelta delta when CanReceiveConversationEvents(current.State) => current with
            {
                State = LiveModeState.Speaking,
                AssistantTranscript = Append(current.AssistantTranscript, delta.Delta),
                Status = "Speaking",
            },
            LiveModeAssistantAudioReceived when CanReceiveConversationEvents(current.State) => current with
            {
                State = LiveModeState.Speaking,
                Status = "Speaking",
            },
            LiveModeResponseDone when CanReceiveConversationEvents(current.State) => current with
            {
                State = LiveModeState.Listening,
                Status = "Listening",
            },
            LiveModeToolStarted started when CanReceiveConversationEvents(current.State) => current with
            {
                State = LiveModeState.ExecutingTool,
                Status = $"Using {RequireText(started.ToolName)}",
            },
            LiveModeToolFinished when current.State == LiveModeState.ExecutingTool => current with
            {
                State = LiveModeState.Thinking,
                Status = "Thinking",
            },
            LiveModeToolFinished when CanReceiveConversationEvents(current.State) => current,
            LiveModeAudioLevelChanged level when current.IsActive => current with
            {
                AudioLevel = current.State == LiveModeState.Stopping
                    ? 0
                    : Math.Clamp(level.Level, 0, 1),
            },
            LiveModeStopRequested when current.IsActive => current with
            {
                State = LiveModeState.Stopping,
                Status = "Stopping",
                AudioLevel = 0,
            },
            LiveModeStopped when current.State is LiveModeState.Stopping
                or LiveModeState.Connecting
                or LiveModeState.Listening
                or LiveModeState.UserSpeaking
                or LiveModeState.Thinking
                or LiveModeState.Speaking
                or LiveModeState.ExecutingTool => current with
            {
                State = LiveModeState.Ended,
                Status = "Live Mode ended",
                AudioLevel = 0,
            },
            LiveModeFailed failed when current.State != LiveModeState.Idle => current with
            {
                State = LiveModeState.Error,
                Status = "Live Mode unavailable",
                SafeError = RequireText(failed.SafeMessage),
                AudioLevel = 0,
            },
            _ when current.State == LiveModeState.Stopping && IsLateSessionEvent(@event) => current,
            _ => throw new InvalidLiveModeTransitionException(current.State, @event.GetType()),
        };
    }

    private static bool IsLateSessionEvent(LiveModeEvent @event) => @event is
        LiveModeConnected or
        LiveModeSpeechStarted or
        LiveModeSpeechStopped or
        LiveModeUserTranscriptDelta or
        LiveModeUserTranscriptDone or
        LiveModeAssistantTranscriptDelta or
        LiveModeAssistantAudioReceived or
        LiveModeResponseDone or
        LiveModeToolStarted or
        LiveModeToolFinished or
        LiveModeAudioLevelChanged;

    private static bool CanReceiveConversationEvents(LiveModeState state) => state is
        LiveModeState.Listening or
        LiveModeState.UserSpeaking or
        LiveModeState.Thinking or
        LiveModeState.Speaking or
        LiveModeState.ExecutingTool;

    private static bool IsTerminal(LiveModeState state) => state is
        LiveModeState.Idle or
        LiveModeState.Ended or
        LiveModeState.Error;

    private static string Append(string current, string delta)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current + RequireText(delta);
    }

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
