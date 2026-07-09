using WinFlow.Core.Abstractions;
using WinFlow.Core.Models;

namespace WinFlow.Core.Correction;

/// <summary>
/// Orchestrates heuristic gating and LLM correction between STT and injection.
/// Falls back to the raw transcript on timeout or failure.
/// </summary>
public sealed class TranscriptCorrectionService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private readonly Func<CorrectionMode> _modeProvider;
    private readonly ITranscriptCorrector? _corrector;
    private readonly TimeSpan _timeout;

    public TranscriptCorrectionService(
        Func<CorrectionMode> modeProvider,
        ITranscriptCorrector? corrector,
        TimeSpan? timeout = null)
    {
        _modeProvider = modeProvider;
        _corrector = corrector;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<string> ProcessAsync(string raw, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(raw) || _corrector is null)
        {
            return raw;
        }

        CorrectionMode mode = _modeProvider();
        if (mode == CorrectionMode.Off)
        {
            return raw;
        }

        CorrectionIntensity intensity = mode == CorrectionMode.Aggressive
            ? CorrectionIntensity.Aggressive
            : CorrectionIntensity.Standard;

        if (mode == CorrectionMode.AutoCorrect && !CorrectionGate.NeedsCorrection(raw))
        {
            return raw;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            string corrected = await _corrector
                .CorrectAsync(raw, intensity, timeoutCts.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(corrected))
            {
                return raw;
            }

            corrected = corrected.Trim();

            // Guard against drastic truncation (e.g. a misbehaving LLM stopping
            // after one token would otherwise replace the whole dictation).
            // Aggressive correction legitimately shortens text by removing
            // fillers, so the floor is generous: reject only when a non-trivial
            // input (> 20 chars) shrinks below 40% of its original length.
            if (raw.Length > 20 && corrected.Length < raw.Length * 2 / 5)
            {
                return raw;
            }

            // Guard against runaway expansion (max 2× original length + 100 chars).
            int maxLen = Math.Max(raw.Length * 2 + 100, 500);
            if (corrected.Length > maxLen)
            {
                corrected = corrected[..maxLen].TrimEnd();
            }

            return corrected;
        }
        catch
        {
            return raw;
        }
    }
}
