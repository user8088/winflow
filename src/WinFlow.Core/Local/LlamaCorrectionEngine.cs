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
    private readonly LocalModelManager _manager;
    private readonly LocalModelDescriptor _model;
    private readonly Lazy<CorrectionRuntime> _runtime;
    private bool _disposed;

    public LlamaCorrectionEngine(LocalModelManager manager, LocalModelDescriptor? model = null)
    {
        _manager = manager;
        _model = model ?? LocalModelCatalog.Qwen25Correction;
        _runtime = new Lazy<CorrectionRuntime>(BuildRuntime, LazyThreadSafetyMode.ExecutionAndPublication);
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

            CorrectionRuntime runtime = _runtime.Value;
            string systemPrompt = CorrectionPrompts.BuildSystemPrompt(intensity);
            string userMessage = CorrectionPrompts.BuildUserMessage(transcript);

            string prompt = $"""
                <|im_start|>system
                {systemPrompt}
                <|im_start|>user
                {userMessage}
                <|im_start|>assistant
                """;

            var inferenceParams = new InferenceParams
            {
                MaxTokens = 256,
                AntiPrompts = ["", "\n\n"],
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
        if (_runtime.IsValueCreated)
        {
            _runtime.Value.Dispose();
        }
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
