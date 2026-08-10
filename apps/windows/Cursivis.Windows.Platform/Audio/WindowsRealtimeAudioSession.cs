using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cursivis.Application.Realtime;
using NAudio;
using NAudio.Wave;

namespace Cursivis.Windows.Platform.Audio;

public sealed class WindowsRealtimeAudioSessionFactory : IRealtimeAudioSessionFactory
{
    private readonly int _deviceNumber;

    public WindowsRealtimeAudioSessionFactory(int deviceNumber = 0)
    {
        _deviceNumber = deviceNumber;
    }

    public Task<IRealtimeAudioSession> OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IRealtimeAudioSession>(new WindowsRealtimeAudioSession(_deviceNumber));
    }
}

/// <summary>
/// A single microphone/speaker lease for one Realtime session. Both directions
/// use the Realtime PCM contract: 24 kHz, mono, signed 16-bit little-endian.
/// </summary>
internal sealed class WindowsRealtimeAudioSession : IRealtimeAudioSession
{
    private const int SampleRate = 24_000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    // 100 ms matches the stable cadence used by the keyboard.wtf live path,
    // reduces per-frame WebSocket overhead, and remains responsive for VAD.
    private const int CaptureBufferMilliseconds = 100;
    private const int CaptureFrameCapacity = 16;
    private static readonly TimeSpan PlaybackBufferDuration = TimeSpan.FromSeconds(12);
    private readonly Channel<RealtimeAudioFrame> _captureFrames;
    private readonly WaveInEvent _capture;
    private readonly WaveOutEvent _playback;
    private readonly BufferedWaveProvider _playbackBuffer;
    private readonly object _playbackGate = new();
    private int _pendingPlaybackByte = -1;
    private int _readerStarted;
    private int _disposed;

    public WindowsRealtimeAudioSession(int deviceNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Realtime audio requires Windows.");
        }

        _captureFrames = Channel.CreateBounded<RealtimeAudioFrame>(new BoundedChannelOptions(CaptureFrameCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });

        WaveFormat format = new(SampleRate, BitsPerSample, Channels);
        _playbackBuffer = new BufferedWaveProvider(format)
        {
            BufferDuration = PlaybackBufferDuration,
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _playback = new WaveOutEvent
        {
            DesiredLatency = 90,
            NumberOfBuffers = 4,
        };
        _playback.Init(_playbackBuffer);
        _playback.Play();

        _capture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = format,
            BufferMilliseconds = CaptureBufferMilliseconds,
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
            _playback.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<RealtimeAudioFrame> ReadCapturedAudioAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _readerStarted, 1) != 0)
        {
            throw new InvalidOperationException("Realtime microphone audio can only be consumed once.");
        }

        await foreach (RealtimeAudioFrame frame in _captureFrames.Reader
                           .ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public ValueTask QueuePlaybackAsync(
        ReadOnlyMemory<byte> pcm16Mono24Khz,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (pcm16Mono24Khz.IsEmpty)
        {
            throw new ArgumentException("Realtime playback audio cannot be empty.", nameof(pcm16Mono24Khz));
        }

        lock (_playbackGate)
        {
            ReadOnlySpan<byte> source = pcm16Mono24Khz.Span;
            int prefix = _pendingPlaybackByte >= 0 ? 1 : 0;
            int playableLength = (source.Length + prefix) & ~1;
            if (playableLength == 0)
            {
                _pendingPlaybackByte = source[0];
                return ValueTask.CompletedTask;
            }

            byte[] copy = GC.AllocateUninitializedArray<byte>(playableLength);
            int destinationOffset = 0;
            if (_pendingPlaybackByte >= 0)
            {
                copy[0] = (byte)_pendingPlaybackByte;
                destinationOffset = 1;
                _pendingPlaybackByte = -1;
            }

            int sourceLength = playableLength - destinationOffset;
            source[..sourceLength].CopyTo(copy.AsSpan(destinationOffset));
            if (sourceLength < source.Length)
            {
                _pendingPlaybackByte = source[^1];
            }

            _playbackBuffer.AddSamples(copy, 0, copy.Length);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ClearPlaybackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_playbackGate)
        {
            _pendingPlaybackByte = -1;
            _playbackBuffer.ClearBuffer();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try
        {
            _capture.StopRecording();
        }
        catch (MmException)
        {
            // The device can already be gone during system shutdown or unplug.
        }

        _capture.Dispose();
        _playback.Stop();
        _playback.Dispose();
        _captureFrames.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 || args.BytesRecorded <= 0)
        {
            return;
        }

        byte[] owned = GC.AllocateUninitializedArray<byte>(args.BytesRecorded);
        args.Buffer.AsSpan(0, args.BytesRecorded).CopyTo(owned);
        _captureFrames.Writer.TryWrite(new RealtimeAudioFrame(owned, CalculateLevel(owned)));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _captureFrames.Writer.TryComplete(args.Exception);
        }
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

        double rms = Math.Sqrt(sumOfSquares / sampleCount);
        return (float)Math.Clamp(rms * 3.2, 0, 1);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
