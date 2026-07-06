using System.Text.RegularExpressions;

namespace WinFlow.Core.Correction;

/// <summary>
/// Cheap heuristic that decides whether a transcript needs LLM correction.
/// Skips already-clean dictation to protect latency (~83% of takes in freeflow).
/// </summary>
public static partial class CorrectionGate
{
    private static readonly string[] Fillers =
    [
        " um ", " uh ", " er ", " ah ", " like ", " you know ", " i mean ",
        " sort of ", " kind of ",
    ];

    private static readonly string[] SelfCorrections =
    [
        "no wait", "wait no", "actually", "i mean", "sorry i", "let me rephrase",
        "what i meant", "or rather",
    ];

    private static readonly string[] IncompleteEndings =
    [
        " and", " the", " to", " a", " an", " or", " but", " so", " because",
        " if", " when", " that", " which",
    ];

    public static bool NeedsCorrection(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        int score = 0;
        string lower = $" {transcript.ToLowerInvariant()} ";

        foreach (string filler in Fillers)
        {
            if (lower.Contains(filler, StringComparison.Ordinal))
            {
                score += 2;
            }
        }

        foreach (string marker in SelfCorrections)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
            {
                score += 3;
            }
        }

        if (RepeatedWordPattern().IsMatch(transcript))
        {
            score += 2;
        }

        string trimmed = transcript.TrimEnd();
        if (trimmed.Length > 40
            && !trimmed.EndsWith('.') && !trimmed.EndsWith('!') && !trimmed.EndsWith('?'))
        {
            score += 1;
        }

        string lowerTrimmed = trimmed.ToLowerInvariant();
        foreach (string ending in IncompleteEndings)
        {
            if (lowerTrimmed.EndsWith(ending, StringComparison.Ordinal))
            {
                score += 2;
                break;
            }
        }

        if (BrokenArticlePattern().IsMatch(lower))
        {
            score += 2;
        }

        return score >= 2;
    }

    [GeneratedRegex(@"\b(\w+)\s+\1\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedWordPattern();

    [GeneratedRegex(@"\b(a)\s+(a|the|an)\b|\b(the)\s+(a|the)\b")]
    private static partial Regex BrokenArticlePattern();
}
