using System.Diagnostics;
using System.Globalization;
using System.Text;
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

    private const int ChunkedCorrectionThreshold = 2000;
    private const int MaxChunkChars = 1500;

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

        if (raw.Length > ChunkedCorrectionThreshold)
        {
            return await ProcessChunkedAsync(raw, intensity, cancellationToken).ConfigureAwait(false);
        }

        return await CorrectOnceAsync(raw, intensity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ProcessChunkedAsync(
        string raw,
        CorrectionIntensity intensity,
        CancellationToken cancellationToken)
    {
        List<string> chunks = SplitIntoChunks(raw, MaxChunkChars);
        if (chunks.Count <= 1)
        {
            return await CorrectOnceAsync(raw, intensity, cancellationToken).ConfigureAwait(false);
        }

        var corrected = new string[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            corrected[i] = await CorrectOnceAsync(chunks[i], intensity, cancellationToken)
                .ConfigureAwait(false);
        }

        return string.Join(" ", corrected);
    }

    private async Task<string> CorrectOnceAsync(
        string raw,
        CorrectionIntensity intensity,
        CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan effectiveTimeout = _timeout
                + TimeSpan.FromSeconds(Math.Min(raw.Length / 150.0, 45));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            string corrected = await _corrector!
                .CorrectAsync(raw, intensity, timeoutCts.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(corrected))
            {
                return raw;
            }

            string? sanitized = SanitizeModelOutput(corrected);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return raw;
            }

            corrected = sanitized;

            if (raw.Length > 20 && corrected.Length < raw.Length * 2 / 5)
            {
                return raw;
            }

            int maxLen = Math.Max(raw.Length * 2 + 100, 500);
            if (corrected.Length > maxLen)
            {
                corrected = TruncateToMaxChars(corrected, maxLen).TrimEnd();
            }

            return corrected;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Transcript correction failed: {exception}");
            return raw;
        }
    }

    internal static List<string> SplitIntoChunks(string text, int maxChunkChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (string sentence in SplitSentences(text))
        {
            if (sentence.Length > maxChunkChars)
            {
                FlushCurrent();
                foreach (string hardPart in HardSplit(sentence, maxChunkChars))
                {
                    chunks.Add(hardPart);
                }

                continue;
            }

            if (current.Length > 0 && current.Length + sentence.Length + 1 > maxChunkChars)
            {
                FlushCurrent();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(sentence);
        }

        FlushCurrent();
        return chunks;

        void FlushCurrent()
        {
            if (current.Length == 0)
            {
                return;
            }

            chunks.Add(current.ToString().Trim());
            current.Clear();
        }
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var current = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            current.Append(c);

            if (c is '.' or '!' or '?' or '\n' or '\r')
            {
                string sentence = current.ToString().Trim();
                if (sentence.Length > 0)
                {
                    yield return sentence;
                }

                current.Clear();
            }
        }

        string tail = current.ToString().Trim();
        if (tail.Length > 0)
        {
            yield return tail;
        }
    }

    private static IEnumerable<string> HardSplit(string text, int maxChunkChars)
    {
        for (int i = 0; i < text.Length; i += maxChunkChars)
        {
            int length = Math.Min(maxChunkChars, text.Length - i);
            yield return text.Substring(i, length).Trim();
        }
    }

    private static readonly string[] ChattyPreambles = ["here is", "sure", "the corrected"];

    private static string? SanitizeModelOutput(string corrected)
    {
        string text = corrected.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal)
            && text.EndsWith("```", StringComparison.Ordinal)
            && text.Length > 6)
        {
            text = text[3..^3];

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

    private static string TruncateToMaxChars(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var info = new StringInfo(text);
        for (int elements = info.LengthInTextElements; elements > 0; elements--)
        {
            string candidate = info.SubstringByTextElements(0, elements);
            if (candidate.Length <= maxChars)
            {
                return candidate;
            }
        }

        return string.Empty;
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
