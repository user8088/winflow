using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Models;

namespace WinFlow.Core.Audio;

/// <summary>
/// Captures the default microphone via WASAPI (shared mode, event-driven)
/// and converts the device's native format to the pipeline's canonical
/// 16-bit mono 24 kHz PCM on the fly:
///
///   WasapiCapture → BufferedWaveProvider → mono downmix → WDL resampler → pcm16
///
/// Conversion happens per DataAvailable callback so downstream consumers
/// (streaming transcription, HUD metering) see chunks in near-real-time.
/// </summary>
public sealed class WasapiAudioProvider : IAudioProvider
{
    public const int TargetSampleRate = 24000;

    /// <summary>Default cap for hold-to-talk; prevents unbounded in-memory accumulation.</summary>
    public static readonly TimeSpan DefaultMaxRecordingDuration = TimeSpan.FromMinutes(5);

    // 16-bit mono PCM at TargetSampleRate for DefaultMaxRecordingDuration.
    public static readonly int MaxRecordingBytes =
        TargetSampleRate * 2 * (int)DefaultMaxRecordingDuration.TotalSeconds;

    // A device-invalidated stop (Bluetooth disconnect, USB unplug) must not
    // discard the take: if at least this much audio was accumulated (~0.3 s of
    // 16-bit mono PCM), StopAsync returns the partial capture instead of
    // throwing, and the pipeline transcribes what was recorded.
    private const int MinUsablePartialBytes = TargetSampleRate * 2 * 3 / 10;

    private readonly Lock _gate = new();

    private NAudio.CoreAudioApi.WasapiCapture? _capture;
    private BufferedWaveProvider? _deviceBuffer;
    private ISampleProvider? _converted;
    private MemoryStream? _accumulated;
    private float[] _floatBuffer = new float[TargetSampleRate]; // 1s of headroom
    private byte[] _pcmBuffer = new byte[TargetSampleRate * 2];
    private float _peakRms;
    private bool _recordingCapped;
    private TaskCompletionSource<Exception?>? _stopped;

    public event Action<AudioChunk>? ChunkAvailable;

    /// <summary>
    /// Opens and immediately closes the default capture device so the first
    /// real dictation skips WASAPI activation and NAudio cold-start cost.
    /// </summary>
    public async Task WarmUpDeviceAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_capture is not null)
            {
                return;
            }
        }

        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            await StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort; the first dictation will retry device activation.
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Device activation can block for tens of milliseconds; keep it off the caller's thread.
        return Task.Run(() =>
        {
            lock (_gate)
            {
                if (_capture is not null)
                {
                    throw new InvalidOperationException("Capture is already running.");
                }

                var capture = new NAudio.CoreAudioApi.WasapiCapture();
                var started = false;

                try
                {
                    _deviceBuffer = new BufferedWaveProvider(capture.WaveFormat)
                    {
                        ReadFully = false,
                        BufferDuration = TimeSpan.FromSeconds(10),
                        DiscardOnBufferOverflow = true,
                    };

                    ISampleProvider samples = _deviceBuffer.ToSampleProvider();
                    if (capture.WaveFormat.Channels > 1)
                    {
                        samples = new MonoAverageSampleProvider(samples);
                    }

                    _converted = new WdlResamplingSampleProvider(samples, TargetSampleRate);
                    _accumulated = new MemoryStream();
                    _peakRms = 0;
                    _recordingCapped = false;
                    _stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

                    capture.DataAvailable += OnDataAvailable;
                    capture.RecordingStopped += OnRecordingStopped;
                    capture.StartRecording();
                    _capture = capture;
                    started = true;
                }
                finally
                {
                    if (!started)
                    {
                        capture.DataAvailable -= OnDataAvailable;
                        capture.RecordingStopped -= OnRecordingStopped;
                        capture.Dispose();
                    }
                }
            }
        }, cancellationToken);
    }

    public async Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        NAudio.CoreAudioApi.WasapiCapture capture;
        TaskCompletionSource<Exception?> stopped;

        lock (_gate)
        {
            if (_capture is null || _stopped is null)
            {
                throw new InvalidOperationException("Capture is not running.");
            }

            capture = _capture;
            stopped = _stopped;
        }

        capture.StopRecording();
        Exception? failure = await stopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            DrainConverted();

            byte[] pcm = _accumulated!.ToArray();
            var result = new CapturedAudio(
                pcm,
                TargetSampleRate,
                TimeSpan.FromSeconds(pcm.Length / 2.0 / TargetSampleRate),
                _peakRms);

            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            _capture = null;
            _deviceBuffer = null;
            _converted = null;
            _accumulated = null;
            _stopped = null;

            // Audio must degrade, not crash: if the device died mid-recording
            // but we already accumulated a usable amount of audio, return the
            // partial take so the user's speech still gets transcribed. Only
            // throw when there is essentially nothing to salvage.
            if (failure is not null && pcm.Length < MinUsablePartialBytes)
            {
                throw new InvalidOperationException("Audio capture stopped with an error.", failure);
            }

            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_deviceBuffer is null)
            {
                return;
            }

            _deviceBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            DrainConverted();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _stopped?.TrySetResult(e.Exception);
    }

    // Must be called under _gate.
    private void DrainConverted()
    {
        if (_converted is null || _accumulated is null)
        {
            return;
        }

        int read;
        while ((read = _converted.Read(_floatBuffer, 0, _floatBuffer.Length)) > 0)
        {
            if (_accumulated.Length >= MaxRecordingBytes)
            {
                if (!_recordingCapped)
                {
                    _recordingCapped = true;
                    _capture?.StopRecording();
                }

                return;
            }

            int pcmBytes = read * 2;
            if (_pcmBuffer.Length < pcmBytes)
            {
                _pcmBuffer = new byte[pcmBytes];
            }

            Pcm16Codec.WriteFloatSamples(_floatBuffer.AsSpan(0, read), _pcmBuffer.AsSpan(0, pcmBytes));

            int writable = Math.Min(pcmBytes, MaxRecordingBytes - (int)_accumulated.Length);
            if (writable <= 0)
            {
                return;
            }

            _accumulated.Write(_pcmBuffer, 0, writable);

            ReadOnlySpan<byte> pcmSpan = _pcmBuffer.AsSpan(0, writable);
            float rms = AudioLevelAnalyzer.ComputeRms(pcmSpan);
            if (rms > _peakRms)
            {
                _peakRms = rms;
            }

            if (ChunkAvailable is not null)
            {
                ChunkAvailable.Invoke(new AudioChunk(pcmSpan.ToArray(), rms));
            }

            if (_accumulated.Length >= MaxRecordingBytes && !_recordingCapped)
            {
                _recordingCapped = true;
                _capture?.StopRecording();
            }
        }
    }
}
