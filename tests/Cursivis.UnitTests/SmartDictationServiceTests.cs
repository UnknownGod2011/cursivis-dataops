using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Cursivis.Application.Context;
using Cursivis.Application.Dictation;
using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Context;
using Cursivis.Domain.Models;

namespace Cursivis.UnitTests;

public sealed class SmartDictationServiceTests
{
    [Fact]
    public async Task NaturalPause_TranscribesPolishesAndInsertsExactlyOnce()
    {
        var capture = new FakeCapture(CreateUtterance());
        var transcription = new RecordingTranscriptionGateway("launch the update tomorrow");
        var responses = new StaticResponsesGateway(
            StructuredResponseResult.Success("{\"text\":\"Launch the update tomorrow.\"}", ModelCatalog.Balanced.Value, "response-1"));
        var insertion = new RecordingInsertionService(TextInsertionStatus.Inserted);
        var clipboard = new RecordingClipboardService(ResultClipboardWriteStatus.Copied);
        await using var service = CreateService(capture, transcription, responses, insertion, clipboard);

        await service.StartAsync();
        await WaitForTerminalStateAsync(service);
        await WaitUntilAsync(() => capture.Disposed);

        Assert.Equal(SmartDictationState.Done, service.Snapshot.State);
        Assert.Equal("Launch the update tomorrow.", service.Snapshot.Text);
        Assert.True(service.Snapshot.Inserted);
        Assert.False(service.Snapshot.CopiedToClipboard);
        Assert.Equal(1, transcription.CallCount);
        Assert.Equal(1, responses.CallCount);
        Assert.Equal(1, insertion.CallCount);
        Assert.Equal(0, clipboard.CallCount);
        Assert.True(capture.StopCount >= 1);
        AssertWave(transcription.LastAudio.Span);
    }

    [Fact]
    public async Task UnsupportedInsertion_CopiesFinalTextExactlyOnce()
    {
        var capture = new FakeCapture(CreateUtterance());
        var insertion = new RecordingInsertionService(TextInsertionStatus.UnsupportedTarget);
        var clipboard = new RecordingClipboardService(ResultClipboardWriteStatus.Copied);
        await using var service = CreateService(
            capture,
            new RecordingTranscriptionGateway("raw words"),
            new StaticResponsesGateway(
                StructuredResponseResult.Success("{\"text\":\"Useful final words.\"}", ModelCatalog.Balanced.Value, "response-2")),
            insertion,
            clipboard);

        await service.StartAsync();
        await WaitForTerminalStateAsync(service);
        await WaitUntilAsync(() => capture.Disposed);

        Assert.Equal(SmartDictationState.Done, service.Snapshot.State);
        Assert.False(service.Snapshot.Inserted);
        Assert.True(service.Snapshot.CopiedToClipboard);
        Assert.Equal("Useful final words.", clipboard.LastText);
        Assert.Equal(1, insertion.CallCount);
        Assert.Equal(1, clipboard.CallCount);
    }

    [Fact]
    public async Task CleanupFailure_UsesValidatedRawTranscript()
    {
        var capture = new FakeCapture(CreateUtterance());
        var insertion = new RecordingInsertionService(TextInsertionStatus.Inserted);
        await using var service = CreateService(
            capture,
            new RecordingTranscriptionGateway("Keep this exact transcript"),
            new StaticResponsesGateway(StructuredResponseResult.Failed(new OpenAiFailure(
                OpenAiFailureKind.Network,
                "Temporary failure.",
                true))),
            insertion,
            new RecordingClipboardService(ResultClipboardWriteStatus.Copied));

        await service.StartAsync();
        await WaitForTerminalStateAsync(service);
        await WaitUntilAsync(() => capture.Disposed);

        Assert.Equal(SmartDictationState.Done, service.Snapshot.State);
        Assert.Equal("Keep this exact transcript", service.Snapshot.Text);
        Assert.Equal("Keep this exact transcript", insertion.LastText);
    }

    [Fact]
    public async Task Cancel_StopsAndDisposesCaptureWithoutModelOrSideEffects()
    {
        var capture = new FakeCapture([SpeechFrame()], holdOpen: true);
        var transcription = new RecordingTranscriptionGateway("unused");
        var responses = new StaticResponsesGateway(
            StructuredResponseResult.Success("{\"text\":\"unused\"}", ModelCatalog.Balanced.Value, "unused"));
        var insertion = new RecordingInsertionService(TextInsertionStatus.Inserted);
        var clipboard = new RecordingClipboardService(ResultClipboardWriteStatus.Copied);
        await using var service = CreateService(capture, transcription, responses, insertion, clipboard);

        await service.StartAsync();
        await WaitUntilAsync(() => service.Snapshot.State == SmartDictationState.Listening);
        await service.CancelAsync();

        Assert.Equal(SmartDictationState.Cancelled, service.Snapshot.State);
        Assert.True(capture.Disposed);
        Assert.Equal(0, transcription.CallCount);
        Assert.Equal(0, responses.CallCount);
        Assert.Equal(0, insertion.CallCount);
        Assert.Equal(0, clipboard.CallCount);
    }

    [Fact]
    public async Task SecondToggle_StopsOpenCaptureAndFinishesDictation()
    {
        var capture = new FakeCapture([SpeechFrame(), SpeechFrame(), SpeechFrame()], holdOpen: true);
        await using var service = CreateService(
            capture,
            new RecordingTranscriptionGateway("manual stop"),
            new StaticResponsesGateway(
                StructuredResponseResult.Success("{\"text\":\"Manual stop.\"}", ModelCatalog.Balanced.Value, "response-3")),
            new RecordingInsertionService(TextInsertionStatus.Inserted),
            new RecordingClipboardService(ResultClipboardWriteStatus.Copied));

        await service.ToggleAsync();
        await WaitUntilAsync(() => capture.FramesRead >= 3);
        await service.ToggleAsync();

        Assert.Equal(SmartDictationState.Done, service.Snapshot.State);
        Assert.True(capture.Disposed);
        Assert.Equal(1, capture.StopCount);
    }

    private static SmartDictationService CreateService(
        FakeCapture capture,
        ITranscriptionGateway transcription,
        IResponsesGateway responses,
        ITextInsertionService insertion,
        IResultClipboardService clipboard) =>
        new(
            new FakeCaptureFactory(capture),
            new StaticTargetProvider(CreateTarget()),
            transcription,
            responses,
            insertion,
            clipboard,
            ModelCatalog.EconomyTranscription,
            ModelCatalog.Balanced);

    private static ContextSnapshot CreateTarget() => ContextSnapshot.FromText(
        ContextKind.Text,
        ContextSource.DirectInput,
        new TargetIdentity("notepad", "1234"),
        "Smart Dictation insertion target",
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(5));

    private static DictationAudioFrame[] CreateUtterance() =>
    [
        SpeechFrame(), SpeechFrame(), SpeechFrame(),
        SilenceFrame(), SilenceFrame(), SilenceFrame(),
        SilenceFrame(), SilenceFrame(), SilenceFrame(),
        SilenceFrame(), SilenceFrame(), SilenceFrame(),
    ];

    private static DictationAudioFrame SpeechFrame()
    {
        byte[] pcm = new byte[4_800];
        for (int offset = 0; offset < pcm.Length; offset += sizeof(short))
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset), 12_000);
        }

        return new DictationAudioFrame(pcm, 0.5f);
    }

    private static DictationAudioFrame SilenceFrame() =>
        new(new byte[4_800], 0);

    private static void AssertWave(ReadOnlySpan<byte> wave)
    {
        Assert.True(wave.Length > 44);
        Assert.Equal("RIFF"u8.ToArray(), wave[..4].ToArray());
        Assert.Equal("WAVE"u8.ToArray(), wave.Slice(8, 4).ToArray());
        Assert.Equal(24_000, BinaryPrimitives.ReadInt32LittleEndian(wave[24..]));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wave[22..]));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wave[34..]));
    }

    private static async Task WaitForTerminalStateAsync(SmartDictationService service) =>
        await WaitUntilAsync(() => service.Snapshot.State is
            SmartDictationState.Done or
            SmartDictationState.Cancelled or
            SmartDictationState.Error);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeCaptureFactory(FakeCapture capture) : IDictationAudioCaptureFactory
    {
        public Task<IDictationAudioCapture> OpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IDictationAudioCapture>(capture);
        }
    }

    private sealed class FakeCapture(
        IReadOnlyList<DictationAudioFrame> frames,
        bool holdOpen = false) : IDictationAudioCapture
    {
        private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FramesRead { get; private set; }

        public int StopCount { get; private set; }

        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<DictationAudioFrame> ReadCapturedAudioAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (DictationAudioFrame frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FramesRead++;
                yield return frame;
                await Task.Yield();
            }

            if (holdOpen)
            {
                await _stop.Task.WaitAsync(cancellationToken);
            }
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            _stop.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _stop.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticTargetProvider(ContextSnapshot target) : IDictationTargetProvider
    {
        public Task<ContextSnapshot?> CaptureAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ContextSnapshot?>(target);
        }
    }

    private sealed class RecordingTranscriptionGateway(string text) : ITranscriptionGateway
    {
        public int CallCount { get; private set; }

        public ReadOnlyMemory<byte> LastAudio { get; private set; }

        public Task<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastAudio = request.Audio;
            return Task.FromResult(TranscriptionResult.Success(text));
        }
    }

    private sealed class StaticResponsesGateway(StructuredResponseResult response) : IResponsesGateway
    {
        public int CallCount { get; private set; }

        public Task<StructuredResponseResult> CreateStructuredResponseAsync(
            StructuredResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(response);
        }

        public Task<ModelAvailabilityResult> CheckModelAvailabilityAsync(
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingInsertionService(TextInsertionStatus status) : ITextInsertionService
    {
        public int CallCount { get; private set; }

        public string? LastText { get; private set; }

        public Task<TextInsertionResult> InsertAsync(
            ContextSnapshot expectedContext,
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastText = text;
            return Task.FromResult(new TextInsertionResult(status, "test"));
        }
    }

    private sealed class RecordingClipboardService(ResultClipboardWriteStatus status) : IResultClipboardService
    {
        public int CallCount { get; private set; }

        public string? LastText { get; private set; }

        public Task<ResultClipboardWriteResult> CopyTextAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastText = text;
            return Task.FromResult(new ResultClipboardWriteResult(status, "test"));
        }
    }
}
