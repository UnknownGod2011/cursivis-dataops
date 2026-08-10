using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cursivis.Application.Dictation;
using NAudio;
using NAudio.Wave;

namespace Cursivis.Windows.Platform.Audio;

public sealed class WindowsDictationAudioCaptureFactory : IDictationAudioCaptureFactory
{
    private readonly int _deviceNumber;

    public WindowsDictationAudioCaptureFactory(int deviceNumber = 0)
    {
        _deviceNumber = deviceNumber;
    }

    public Task<IDictationAudioCapture> OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IDictationAudioCapture>(new WindowsDictationAudioCapture(_deviceNumber));
    }
}

/// <summary>
/// A capture-only microphone lease for Smart Dictation. Unlike Live Mode it
/// creates no playback device and never opens a Realtime session.
/// </summary>
internal sealed class WindowsDictationAudioCapture : IDictationAudioCapture
{
    private const int SampleRate = 24_000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BufferMilliseconds = 100;
    private readonly Channel<DictationAudioFrame> _frames;
    private readonly WaveInEvent _capture;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readerStarted;
    private int _stopRequested;
    private int _disposed;

    public WindowsDictationAudioCapture(int deviceNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Smart Dictation audio capture requires Windows.");
        }

        _frames = Channel.CreateBounded<DictationAudioFrame>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _capture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = BufferMilliseconds,
            NumberOfBuffers = 4,
        };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        try
        {
            _capture.StartRecording();
        }
        catch
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<DictationAudioFrame> ReadCapturedAudioAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _readerStarted, 1) != 0)
        {
            throw new InvalidOperationException("Smart Dictation microphone audio can only be consumed once.");
        }

        await foreach (DictationAudioFrame frame in _frames.Reader
                           .ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
        {
            try
            {
                _capture.StopRecording();
            }
            catch (MmException)
            {
                _frames.Writer.TryComplete();
                _stopped.TrySetResult();
            }
        }

        await _stopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
        {
            try
            {
                _capture.StopRecording();
            }
            catch (MmException)
            {
                _stopped.TrySetResult();
            }
        }

        try
        {
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Device removal can prevent NAudio from raising RecordingStopped.
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _frames.Writer.TryComplete();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 || args.BytesRecorded <= 0)
        {
            return;
        }

        byte[] owned = GC.AllocateUninitializedArray<byte>(args.BytesRecorded);
        args.Buffer.AsSpan(0, args.BytesRecorded).CopyTo(owned);
        _frames.Writer.TryWrite(new DictationAudioFrame(owned, CalculateLevel(owned)));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        _frames.Writer.TryComplete(args.Exception);
        _stopped.TrySetResult();
    }

    private static float CalculateLevel(ReadOnlySpan<byte> pcm16)
    {
        int sampleCount = pcm16.Length / sizeof(short);
        if (sampleCount == 0)
        {
            return 0;
        }

        double sumOfSquares = 0;
        for (int offset = 0; offset + 1 < pcm16.Length; offset += sizeof(short))
        {
            double normalized = BinaryPrimitives.ReadInt16LittleEndian(pcm16[offset..]) / 32768d;
            sumOfSquares += normalized * normalized;
        }

        return (float)Math.Clamp(Math.Sqrt(sumOfSquares / sampleCount) * 3.2, 0, 1);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
