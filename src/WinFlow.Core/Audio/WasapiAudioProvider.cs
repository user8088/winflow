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

    private readonly Lock _gate = new();

    private NAudio.CoreAudioApi.WasapiCapture? _capture;
    private BufferedWaveProvider? _deviceBuffer;
    private ISampleProvider? _converted;
    private MemoryStream? _accumulated;
    private float[] _floatBuffer = new float[TargetSampleRate]; // 1s of headroom
    private float _peakRms;
    private TaskCompletionSource<Exception?>? _stopped;

    public event Action<AudioChunk>? ChunkAvailable;

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
                _stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();
                _capture = capture;
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

            if (failure is not null)
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
            byte[] pcm = new byte[read * 2];
            for (int i = 0; i < read; i++)
            {
                float clamped = Math.Clamp(_floatBuffer[i], -1f, 1f);
                short sample = (short)(clamped * short.MaxValue);
                pcm[i * 2] = (byte)(sample & 0xFF);
                pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            _accumulated.Write(pcm, 0, pcm.Length);

            float rms = AudioLevelAnalyzer.ComputeRms(pcm);
            if (rms > _peakRms)
            {
                _peakRms = rms;
            }

            ChunkAvailable?.Invoke(new AudioChunk(pcm, rms));
        }
    }
}
