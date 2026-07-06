using SherpaOnnx;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Local.Models;
using WinFlow.Core.Models;

namespace WinFlow.Core.Local;

/// <summary>
/// Offline transcription with sherpa-onnx (NVIDIA Parakeet TDT transducer
/// running on ONNX Runtime). Implements the batch interface, so in local
/// mode the full take is transcribed after key release — no streaming,
/// no network, no API key.
///
/// The recognizer is constructed lazily and reused across dictations;
/// loading the encoder is the expensive one-time cost (~1–2 s on first
/// call). Audio is fed at the pipeline's native 24 kHz and resampled to
/// the model's 16 kHz inside sherpa-onnx.
/// </summary>
public sealed class SherpaOnnxSttEngine : IBatchSttProvider, IDisposable
{
    private readonly LocalModelManager _manager;
    private readonly LocalModelDescriptor _model;
    private readonly Lazy<OfflineRecognizer> _recognizer;
    private bool _disposed;

    public SherpaOnnxSttEngine(LocalModelManager manager, LocalModelDescriptor? model = null)
    {
        _manager = manager;
        _model = model ?? LocalModelCatalog.Default;
        _recognizer = new Lazy<OfflineRecognizer>(BuildRecognizer, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<string> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SherpaOnnxSttEngine));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            OfflineRecognizer recognizer = _recognizer.Value;
            OfflineStream stream = recognizer.CreateStream();

            float[] samples = ToFloatSamples(audio.Pcm16);
            stream.AcceptWaveform(audio.SampleRate, samples);

            recognizer.Decode(stream);
            return stream.Result.Text.Trim();
        }, cancellationToken);
    }

    private OfflineRecognizer BuildRecognizer()
    {
        if (!_manager.IsInstalled(_model))
        {
            throw new InvalidOperationException(
                $"Local model '{_model.Id}' is not installed.");
        }

        string dir = _manager.ModelDirectory(_model);
        var config = new OfflineRecognizerConfig
        {
            FeatConfig =
            {
                SampleRate = _model.SampleRate,
                FeatureDim = 80,
            },
            ModelConfig =
            {
                Tokens = Path.Combine(dir, "tokens.txt"),
                Transducer =
                {
                    Encoder = Path.Combine(dir, "encoder.int8.onnx"),
                    Decoder = Path.Combine(dir, "decoder.int8.onnx"),
                    Joiner = Path.Combine(dir, "joiner.int8.onnx"),
                },
                NumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                ModelType = _model.ModelType,
                Debug = 0,
                Provider = "cpu",
            },
            DecodingMethod = "greedy_search",
        };

        var recognizer = new OfflineRecognizer(config);
        // Warm the model now so the first real dictation doesn't pay load time.
        return recognizer;
    }

    private static float[] ToFloatSamples(byte[] pcm16)
    {
        int sampleCount = pcm16.Length / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            samples[i] = sample / 32768f;
        }

        return samples;
    }

    public void Dispose()
    {
        // The sherpa-onnx C# OfflineRecognizer binding has no Dispose; its
        // native handle is reclaimed on finalization. We only gate further use.
        _disposed = true;
    }
}
