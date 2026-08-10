using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Context;

namespace Cursivis.Application.Realtime;

public enum LiveModeState
{
    Idle,
    Connecting,
    Listening,
    UserSpeaking,
    Thinking,
    Speaking,
    ExecutingTool,
    Stopping,
    Ended,
    Error,
}

public sealed record LiveModeSnapshot(
    LiveModeState State,
    string UserTranscript,
    string AssistantTranscript,
    float AudioLevel,
    string Status,
    string? SafeError = null)
{
    public static LiveModeSnapshot Initial { get; } = new(
        LiveModeState.Idle,
        string.Empty,
        string.Empty,
        0,
        "Ready");

    public bool IsActive => State is not LiveModeState.Idle
        and not LiveModeState.Ended
        and not LiveModeState.Error;
}

public sealed record LiveModeContext(
    string? SelectedText,
    string? ContextFingerprint,
    string? ActiveApplication,
    string? ActiveWindowTitle,
    ContextSnapshot? CapturedContext = null)
{
    public static LiveModeContext Empty { get; } = new(null, null, null, null, null);
}

public sealed record LiveModeMemoryEntry(
    Guid Id,
    string Text,
    DateTimeOffset CreatedAtUtc);

public sealed record LiveModeMemorySnapshot(
    bool IsEnabled,
    IReadOnlyList<LiveModeMemoryEntry> Entries);

public enum LiveModeMemorySaveStatus
{
    Saved,
    Disabled,
    Rejected,
}

public sealed record LiveModeMemorySaveResult(
    LiveModeMemorySaveStatus Status,
    LiveModeMemoryEntry? Entry,
    string SafeMessage);

public interface ILiveModeMemoryStore
{
    Task<LiveModeMemorySnapshot> GetAsync(CancellationToken cancellationToken = default);

    Task<LiveModeMemorySnapshot> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<LiveModeMemorySaveResult> RememberExplicitAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed record LiveModeCapabilityResult(
    bool Succeeded,
    string SafeMessage,
    string? Output = null);

public interface ILiveModeCapabilityExecutor
{
    Task<LiveModeCapabilityResult> CopyAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<LiveModeCapabilityResult> InsertAsync(
        LiveModeContext context,
        string text,
        CancellationToken cancellationToken = default);

    Task<LiveModeCapabilityResult> AnalyzeScreenAsync(
        string instruction,
        CancellationToken cancellationToken = default);

    Task<LiveModeCapabilityResult> TakeBrowserActionAsync(
        string instruction,
        CancellationToken cancellationToken = default);

    Task<LiveModeCapabilityResult> NavigateAsync(
        string instruction,
        CancellationToken cancellationToken = default);
}

public interface ILiveModeContextProvider
{
    Task<LiveModeContext> CaptureAsync(CancellationToken cancellationToken = default);
}

public readonly record struct RealtimeAudioFrame(
    ReadOnlyMemory<byte> Pcm16Mono24Khz,
    float Level);

public interface IRealtimeAudioSession : IAsyncDisposable
{
    IAsyncEnumerable<RealtimeAudioFrame> ReadCapturedAudioAsync(
        CancellationToken cancellationToken = default);

    ValueTask QueuePlaybackAsync(
        ReadOnlyMemory<byte> pcm16Mono24Khz,
        CancellationToken cancellationToken = default);

    ValueTask ClearPlaybackAsync(CancellationToken cancellationToken = default);
}

public interface IRealtimeAudioSessionFactory
{
    Task<IRealtimeAudioSession> OpenAsync(CancellationToken cancellationToken = default);
}

public sealed record LiveModeToolExecutionResult(
    string Json,
    bool StopSession = false);

public interface ILiveModeToolExecutor
{
    IReadOnlyList<RealtimeToolDefinition> Definitions { get; }

    ValueTask<LiveModeToolExecutionResult> ExecuteAsync(
        LiveModeContext context,
        string toolName,
        string untrustedArgumentsJson,
        CancellationToken cancellationToken = default);
}

public interface IRealtimeLiveModeService : IAsyncDisposable
{
    LiveModeSnapshot Snapshot { get; }

    event Action<LiveModeSnapshot>? SnapshotChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task ToggleAsync(CancellationToken cancellationToken = default);
}
