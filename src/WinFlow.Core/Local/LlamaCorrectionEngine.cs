using LLama;
using LLama.Common;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Correction;
using WinFlow.Core.Local.Models;

namespace WinFlow.Core.Local;

/// <summary>
/// Offline transcript correction with a small Qwen GGUF model via LLamaSharp.
/// The model loads lazily on first use (~1–2 s).
/// </summary>
public sealed class LlamaCorrectionEngine : ITranscriptCorrector, IDisposable
{
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly LocalModelManager _manager;
    private readonly LocalModelDescriptor _model;

    // Serializes runtime initialization and inference, and lets Dispose wait for
    // an in-flight inference before freeing the native weights.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CorrectionRuntime? _runtime;
    private volatile bool _disposed;

    public LlamaCorrectionEngine(LocalModelManager manager, LocalModelDescriptor? model = null)
    {
        _manager = manager;
        _model = model ?? LocalModelCatalog.Qwen25Correction;
    }

    public Task<string> CorrectAsync(
        string transcript,
        CorrectionIntensity intensity,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LlamaCorrectionEngine));
        }

        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Hold the gate for the whole inference so Dispose cannot free the
            // native weights while InferAsync is still using them.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(LlamaCorrectionEngine));
                }

                // Retryable init: if the model is not installed yet, BuildRuntime
                // throws and _runtime stays null, so the next call re-checks
                // instead of rethrowing a cached exception (unlike Lazy<T>).
                CorrectionRuntime runtime = _runtime ??= BuildRuntime();

                string systemPrompt = CorrectionPrompts.BuildSystemPrompt(intensity);
                string userMessage = CorrectionPrompts.BuildUserMessage(transcript);

                // Qwen2.5 ChatML: every turn is "<|im_start|>role\n...<|im_end|>\n"
                // and the prompt ends with "<|im_start|>assistant\n" so the model
                // generates the assistant turn (AntiPrompts stop on "<|im_end|>").
                string prompt =
                    $"<|im_start|>system\n{systemPrompt}<|im_end|>\n" +
                    $"<|im_start|>user\n{userMessage}<|im_end|>\n" +
                    "<|im_start|>assistant\n";

                var inferenceParams = new InferenceParams
                {
                    MaxTokens = 256,
                    AntiPrompts = ["<|im_end|>", "\n\n"],
                };

                var output = new System.Text.StringBuilder();
                await foreach (string token in runtime.Executor
                    .InferAsync(prompt, inferenceParams, cancellationToken)
                    .ConfigureAwait(false))
                {
                    output.Append(token);
                }

                string result = output.ToString().Trim();
                return string.IsNullOrWhiteSpace(result) ? transcript : result;
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);
    }

    private CorrectionRuntime BuildRuntime()
    {
        if (!_manager.IsInstalled(_model))
        {
            throw new InvalidOperationException(
                $"Local correction model '{_model.Id}' is not installed.");
        }

        string modelPath = Path.Combine(
            _manager.ModelDirectory(_model),
            _model.Files[0].RelativePath);

        var parameters = new ModelParams(modelPath)
        {
            ContextSize = 2048,
            GpuLayerCount = 0,
        };

        var weights = LLamaWeights.LoadFromFile(parameters);
        var executor = new StatelessExecutor(weights, parameters);
        return new CorrectionRuntime(weights, executor);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Wait (bounded) for an in-flight inference to release the gate so we
        // never free the native weights while InferAsync is still using them.
        // If the wait times out, leak the runtime instead of freeing it under
        // an active inference (an access violation would crash the process);
        // the OS reclaims the memory when the app exits.
        if (_gate.Wait(DisposeTimeout))
        {
            try
            {
                _runtime?.Dispose();
                _runtime = null;
            }
            finally
            {
                _gate.Release();
            }
        }

        // _gate is intentionally not disposed: late CorrectAsync callers may
        // still be waiting on it; they observe _disposed and throw.
    }

    private sealed class CorrectionRuntime : IDisposable
    {
        public StatelessExecutor Executor { get; }
        private readonly LLamaWeights _weights;

        public CorrectionRuntime(LLamaWeights weights, StatelessExecutor executor)
        {
            _weights = weights;
            Executor = executor;
        }

        public void Dispose() => _weights.Dispose();
    }
}
