using Cursivis.Domain.Context;

namespace Cursivis.Application.Dictation;

public enum SmartDictationState
{
    Idle,
    Listening,
    Transcribing,
    Polishing,
    Inserting,
    Done,
    Cancelled,
    Error,
}

public sealed record SmartDictationSnapshot(
    SmartDictationState State,
    string Text,
    float AudioLevel,
    string Status,
    string? SafeError = null,
    bool Inserted = false,
    bool CopiedToClipboard = false)
{
    public static SmartDictationSnapshot Initial { get; } = new(
        SmartDictationState.Idle,
        string.Empty,
        0,
        "Ready");

    public bool IsActive => State is
        SmartDictationState.Listening or
        SmartDictationState.Transcribing or
        SmartDictationState.Polishing or
        SmartDictationState.Inserting;
}

public readonly record struct DictationAudioFrame(
    ReadOnlyMemory<byte> Pcm16Mono24Khz,
    float Level);

public interface IDictationAudioCapture : IAsyncDisposable
{
    IAsyncEnumerable<DictationAudioFrame> ReadCapturedAudioAsync(
        CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public interface IDictationAudioCaptureFactory
{
    Task<IDictationAudioCapture> OpenAsync(CancellationToken cancellationToken = default);
}

public interface IDictationTargetProvider
{
    Task<ContextSnapshot?> CaptureAsync(CancellationToken cancellationToken = default);
}

public interface ISmartDictationService : IAsyncDisposable
{
    SmartDictationSnapshot Snapshot { get; }

    event Action<SmartDictationSnapshot>? SnapshotChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task CancelAsync(CancellationToken cancellationToken = default);

    Task ToggleAsync(CancellationToken cancellationToken = default);
}
