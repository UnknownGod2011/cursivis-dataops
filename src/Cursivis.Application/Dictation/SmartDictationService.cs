using System.Buffers.Binary;
using System.Text.Json;
using Cursivis.Application.Context;
using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Context;
using Cursivis.Domain.Models;

namespace Cursivis.Application.Dictation;

/// <summary>
/// Bounded one-shot dictation. It deliberately uses the standard Audio
/// Transcriptions endpoint; continuously streamed Realtime audio remains
/// exclusive to Live Mode.
/// </summary>
public sealed class SmartDictationService : ISmartDictationService
{
    private const int SampleRate = 24_000;
    private const int BytesPerSample = sizeof(short);
    private const int BytesPerSecond = SampleRate * BytesPerSample;
    private const float SpeechLevelThreshold = 0.035f;
    private static readonly TimeSpan MinimumSpeechDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan NaturalPauseDuration = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan NoSpeechTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumCaptureDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TranscriptionTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan PolishTimeout = TimeSpan.FromSeconds(45);
    private readonly IDictationAudioCaptureFactory _audioFactory;
    private readonly IDictationTargetProvider _targetProvider;
    private readonly ITranscriptionGateway _transcription;
    private readonly IResponsesGateway _responses;
    private readonly ITextInsertionService _insertion;
    private readonly IResultClipboardService _clipboard;
    private readonly ModelIdentifier _transcriptionModel;
    private readonly ModelIdentifier _polishModel;
    private readonly object _gate = new();
    private SmartDictationSnapshot _snapshot = SmartDictationSnapshot.Initial;
    private RunContext? _run;
    private bool _disposed;

    public SmartDictationService(
        IDictationAudioCaptureFactory audioFactory,
        IDictationTargetProvider targetProvider,
        ITranscriptionGateway transcription,
        IResponsesGateway responses,
        ITextInsertionService insertion,
        IResultClipboardService clipboard,
        ModelIdentifier transcriptionModel,
        ModelIdentifier polishModel)
    {
        _audioFactory = audioFactory ?? throw new ArgumentNullException(nameof(audioFactory));
        _targetProvider = targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));
        _transcription = transcription ?? throw new ArgumentNullException(nameof(transcription));
        _responses = responses ?? throw new ArgumentNullException(nameof(responses));
        _insertion = insertion ?? throw new ArgumentNullException(nameof(insertion));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _transcriptionModel = transcriptionModel;
        _polishModel = polishModel;

        ModelDescriptor transcriptionDescriptor = ModelCatalog.Find(transcriptionModel)
            ?? throw new ArgumentException("The transcription model is not in the Cursivis model catalog.", nameof(transcriptionModel));
        if (transcriptionDescriptor.Role != ModelRole.Transcription ||
            !transcriptionDescriptor.Capabilities.HasFlag(ModelCapabilities.BoundedTranscription))
        {
            throw new ArgumentException("Smart Dictation requires a bounded transcription model.", nameof(transcriptionModel));
        }

        ModelDescriptor polishDescriptor = ModelCatalog.Find(polishModel)
            ?? throw new ArgumentException("The polish model is not in the Cursivis model catalog.", nameof(polishModel));
        if (polishDescriptor.Role != ModelRole.Responses ||
            !polishDescriptor.Capabilities.HasFlag(ModelCapabilities.StructuredOutputs))
        {
            throw new ArgumentException("Smart Dictation cleanup requires a Responses model with Structured Outputs.", nameof(polishModel));
        }
    }

    public SmartDictationSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public event Action<SmartDictationSnapshot>? SnapshotChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_run is not null)
            {
                return;
            }
        }

        ContextSnapshot? target = await _targetProvider.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            Publish(new SmartDictationSnapshot(
                SmartDictationState.Error,
                string.Empty,
                0,
                "Dictation unavailable",
                "Cursivis could not identify the active text target."));
            return;
        }

        IDictationAudioCapture capture;
        try
        {
            capture = await _audioFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Publish(new SmartDictationSnapshot(
                SmartDictationState.Error,
                string.Empty,
                0,
                "Microphone unavailable",
                "Cursivis could not open the selected microphone."));
            return;
        }

        var run = new RunContext(target, capture, new CancellationTokenSource());
        lock (_gate)
        {
            if (_disposed || _run is not null)
            {
                run.Cancellation.Cancel();
            }
            else
            {
                _run = run;
            }
        }

        if (run.Cancellation.IsCancellationRequested)
        {
            await capture.DisposeAsync().ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            return;
        }

        Publish(new SmartDictationSnapshot(
            SmartDictationState.Listening,
            string.Empty,
            0,
            "Listening"));
        run.Work = ProcessAsync(run);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RunContext? run;
        lock (_gate)
        {
            run = _run;
        }

        if (run is null)
        {
            return;
        }

        await run.Capture.StopAsync(cancellationToken).ConfigureAwait(false);
        await AwaitRunAsync(run, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        RunContext? run;
        lock (_gate)
        {
            run = _run;
        }

        if (run is null)
        {
            return;
        }

        run.Cancellation.Cancel();
        await run.Capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await AwaitRunAsync(run, cancellationToken).ConfigureAwait(false);
    }

    public async Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        if (Snapshot.IsActive)
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CancelAsync().ConfigureAwait(false);
    }

    private async Task ProcessAsync(RunContext run)
    {
        using var pcm = new MemoryStream(capacity: 256 * 1024);
        long speechBytes = 0;
        long trailingSilenceBytes = 0;
        bool speechDetected = false;

        try
        {
            await foreach (DictationAudioFrame frame in run.Capture
                               .ReadCapturedAudioAsync(run.Cancellation.Token)
                               .ConfigureAwait(false))
            {
                if (frame.Pcm16Mono24Khz.IsEmpty)
                {
                    continue;
                }

                await pcm.WriteAsync(frame.Pcm16Mono24Khz, run.Cancellation.Token).ConfigureAwait(false);
                long frameBytes = frame.Pcm16Mono24Khz.Length;
                if (frame.Level >= SpeechLevelThreshold)
                {
                    speechDetected = true;
                    speechBytes += frameBytes;
                    trailingSilenceBytes = 0;
                }
                else if (speechDetected)
                {
                    trailingSilenceBytes += frameBytes;
                }

                Publish(new SmartDictationSnapshot(
                    SmartDictationState.Listening,
                    string.Empty,
                    Math.Clamp(frame.Level, 0, 1),
                    "Listening"));

                TimeSpan totalDuration = DurationFromBytes(pcm.Length);
                bool naturalPause = speechBytes >= BytesFromDuration(MinimumSpeechDuration) &&
                                    trailingSilenceBytes >= BytesFromDuration(NaturalPauseDuration);
                bool noSpeechExpired = !speechDetected && totalDuration >= NoSpeechTimeout;
                if (naturalPause || noSpeechExpired || totalDuration >= MaximumCaptureDuration)
                {
                    await run.Capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            run.Cancellation.Token.ThrowIfCancellationRequested();
            if (!speechDetected || speechBytes < BytesFromDuration(MinimumSpeechDuration))
            {
                Publish(new SmartDictationSnapshot(
                    SmartDictationState.Error,
                    string.Empty,
                    0,
                    "No speech detected",
                    "Try again and speak after the microphone opens."));
                return;
            }

            byte[] wave = BuildWaveFile(pcm.GetBuffer().AsSpan(0, checked((int)pcm.Length)));
            Publish(new SmartDictationSnapshot(
                SmartDictationState.Transcribing,
                string.Empty,
                0,
                "Transcribing"));
            TranscriptionResult transcription = await _transcription.TranscribeAsync(
                new TranscriptionRequest(
                    _transcriptionModel.Value,
                    wave,
                    "audio/wav",
                    Language: null,
                    TranscriptionTimeout),
                run.Cancellation.Token).ConfigureAwait(false);
            if (!transcription.Succeeded || string.IsNullOrWhiteSpace(transcription.Text))
            {
                PublishFailure("Transcription failed", transcription.Failure);
                return;
            }

            string rawTranscript = transcription.Text.Trim();
            Publish(new SmartDictationSnapshot(
                SmartDictationState.Polishing,
                rawTranscript,
                0,
                "Polishing text"));
            string finalText = await PolishAsync(
                rawTranscript,
                run.Target.Target.ApplicationId,
                run.Cancellation.Token).ConfigureAwait(false);

            Publish(new SmartDictationSnapshot(
                SmartDictationState.Inserting,
                finalText,
                0,
                "Inserting"));
            TextInsertionResult insertion;
            try
            {
                insertion = await _insertion.InsertAsync(
                    run.Target,
                    finalText,
                    run.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                insertion = new TextInsertionResult(
                    TextInsertionStatus.Failed,
                    "The target did not accept inserted text.");
            }

            if (insertion.Status == TextInsertionStatus.Inserted)
            {
                Publish(new SmartDictationSnapshot(
                    SmartDictationState.Done,
                    finalText,
                    0,
                    "Inserted",
                    Inserted: true));
                return;
            }

            ResultClipboardWriteResult clipboard = await _clipboard.CopyTextAsync(
                finalText,
                run.Cancellation.Token).ConfigureAwait(false);
            if (clipboard.Status == ResultClipboardWriteStatus.Copied)
            {
                Publish(new SmartDictationSnapshot(
                    SmartDictationState.Done,
                    finalText,
                    0,
                    "Copied",
                    CopiedToClipboard: true));
                return;
            }

            Publish(new SmartDictationSnapshot(
                SmartDictationState.Error,
                finalText,
                0,
                "Text is ready",
                "Cursivis could not insert or copy the dictated text."));
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            Publish(new SmartDictationSnapshot(
                SmartDictationState.Cancelled,
                string.Empty,
                0,
                "Dictation cancelled"));
        }
        catch (Exception)
        {
            Publish(new SmartDictationSnapshot(
                SmartDictationState.Error,
                string.Empty,
                0,
                "Dictation stopped",
                "Cursivis could not finish this dictation."));
        }
        finally
        {
            await run.Capture.DisposeAsync().ConfigureAwait(false);
            run.Cancellation.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_run, run))
                {
                    _run = null;
                }
            }

            run.Completion.TrySetResult();
        }
    }

    private async Task<string> PolishAsync(
        string transcript,
        string targetApplication,
        CancellationToken cancellationToken)
    {
        StructuredResponseResult response = await _responses.CreateStructuredResponseAsync(
            new StructuredResponseRequest(
                _polishModel.Value,
                """
                Convert a raw voice dictation transcript into the exact final text the user intended to type.
                Remove filler and abandoned false starts only when the correction is clear. Preserve meaning,
                nuance, names, numbers, URLs, code, and domain terms. Add natural punctuation and paragraphs.
                Match the likely target application without adding facts, commentary, headings, or quotation marks.
                Return only the validated text object.
                """,
                $"Target application: {targetApplication}\nRaw transcript:\n{transcript}",
                "cursivis_smart_dictation",
                """
                {
                  "type": "object",
                  "properties": {
                    "text": { "type": "string", "minLength": 1, "maxLength": 20000 }
                  },
                  "required": ["text"],
                  "additionalProperties": false
                }
                """,
                PolishTimeout,
                $"dictation-{Guid.NewGuid():N}"),
            cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded || string.IsNullOrWhiteSpace(response.Json))
        {
            return transcript;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Json);
            string? polished = document.RootElement.GetProperty("text").GetString();
            return string.IsNullOrWhiteSpace(polished) ? transcript : polished.Trim();
        }
        catch (JsonException)
        {
            return transcript;
        }
    }

    private void PublishFailure(string status, OpenAiFailure? failure) =>
        Publish(new SmartDictationSnapshot(
            SmartDictationState.Error,
            string.Empty,
            0,
            status,
            failure?.SafeMessage ?? "OpenAI did not return a usable transcript."));

    private void Publish(SmartDictationSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
        }

        SnapshotChanged?.Invoke(snapshot);
    }

    private static async Task AwaitRunAsync(RunContext run, CancellationToken cancellationToken)
    {
        if (run.Work is { } work)
        {
            await work.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await run.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long BytesFromDuration(TimeSpan duration) =>
        checked((long)Math.Ceiling(duration.TotalSeconds * BytesPerSecond));

    private static TimeSpan DurationFromBytes(long bytes) =>
        TimeSpan.FromSeconds(bytes / (double)BytesPerSecond);

    internal static byte[] BuildWaveFile(ReadOnlySpan<byte> pcm16Mono24Khz)
    {
        if (pcm16Mono24Khz.IsEmpty || (pcm16Mono24Khz.Length & 1) != 0)
        {
            throw new ArgumentException("PCM audio must contain complete 16-bit samples.", nameof(pcm16Mono24Khz));
        }

        byte[] wave = GC.AllocateUninitializedArray<byte>(checked(44 + pcm16Mono24Khz.Length));
        Span<byte> header = wave.AsSpan(0, 44);
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], wave.Length - 8);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], BytesPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], pcm16Mono24Khz.Length);
        pcm16Mono24Khz.CopyTo(wave.AsSpan(44));
        return wave;
    }

    private sealed class RunContext(
        ContextSnapshot target,
        IDictationAudioCapture capture,
        CancellationTokenSource cancellation)
    {
        public ContextSnapshot Target { get; } = target;

        public IDictationAudioCapture Capture { get; } = capture;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Work { get; set; }
    }
}
