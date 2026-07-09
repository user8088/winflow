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

            // The model is instructed to return only the corrected text;
            // unwrap cosmetic wrappers and reject contract violations.
            string? sanitized = SanitizeModelOutput(corrected);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return raw;
            }

            corrected = sanitized;

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

    private static readonly string[] ChattyPreambles = ["here is", "sure", "the corrected"];

    /// <summary>
    /// Unwraps cosmetic wrappers the model may add despite instructions
    /// (markdown code fences, one pair of enclosing matched quotes) and
    /// returns null when the output starts with a chatty preamble — that
    /// violates the "output only the corrected text" contract, so the
    /// caller must fall back to the raw transcript.
    /// </summary>
    private static string? SanitizeModelOutput(string corrected)
    {
        string text = corrected.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal)
            && text.EndsWith("```", StringComparison.Ordinal)
            && text.Length > 6)
        {
            text = text[3..^3];

            // Drop a language tag on the opening fence line (e.g. ```text).
            int newline = text.IndexOf('\n');
            if (newline >= 0 && IsFenceLanguageTag(text.AsSpan(0, newline).TrimEnd()))
            {
                text = text[(newline + 1)..];
            }

            text = text.Trim();
        }

        if (text.Length >= 2)
        {
            char first = text[0];
            char last = text[^1];
            bool matchedQuotes =
                (first == '"' && last == '"')
                || (first == '\'' && last == '\'')
                || (first == '\u201C' && last == '\u201D');

            // Only unwrap when the quote characters do not also appear
            // inside, so legitimately quoted dictation is left intact.
            if (matchedQuotes)
            {
                ReadOnlySpan<char> interior = text.AsSpan(1, text.Length - 2);
                if (!interior.Contains(first) && !interior.Contains(last))
                {
                    text = text[1..^1].Trim();
                }
            }
        }

        foreach (string preamble in ChattyPreambles)
        {
            if (text.StartsWith(preamble, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return text;
    }

    private static bool IsFenceLanguageTag(ReadOnlySpan<char> line)
    {
        foreach (char c in line)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
