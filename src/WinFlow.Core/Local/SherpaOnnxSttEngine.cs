using SherpaOnnx;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Audio;
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
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly LocalModelManager _manager;
    private readonly LocalModelDescriptor _model;

    // Serializes runtime initialization and transcription, and lets Dispose
    // wait for an in-flight decode before dropping the cached recognizer.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private volatile bool _disposed;

    public SherpaOnnxSttEngine(LocalModelManager manager, LocalModelDescriptor? model = null)
    {
        _manager = manager;
        _model = model ?? LocalModelCatalog.Default;
    }

    public Task<string> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SherpaOnnxSttEngine));
        }

        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(SherpaOnnxSttEngine));
                }

                // Retryable init: if the model is not installed yet, BuildRecognizer
                // throws and _recognizer stays null, so the next call re-checks
                // instead of rethrowing a cached exception (unlike Lazy<T>).
                OfflineRecognizer recognizer = _recognizer ??= BuildRecognizer();

                using OfflineStream stream = recognizer.CreateStream();

                float[] samples = Pcm16Codec.ToFloatSamples(audio.Pcm16);
                stream.AcceptWaveform(audio.SampleRate, samples);

                recognizer.Decode(stream);
                return stream.Result.Text.Trim();
            }
            finally
            {
                _gate.Release();
            }
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
        // ONNX encoder/decoder/joiner load here on first TranscribeAsync call.
        return recognizer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Wait (bounded) for an in-flight transcription to release the gate
        // before dropping the cached recognizer. The sherpa-onnx C# binding
        // has no Dispose; its native handle is reclaimed on finalization.
        if (_gate.Wait(DisposeTimeout))
        {
            _recognizer = null;
            _gate.Release();
        }

        // _gate is intentionally not disposed: late TranscribeAsync callers may
        // still be waiting on it; they observe _disposed and throw.
    }
}
