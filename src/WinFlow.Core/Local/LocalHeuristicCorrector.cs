using System.Text.RegularExpressions;
using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Local;

/// <summary>
/// Offline transcript cleanup using deterministic rules: removes fillers,
/// collapses stutters, fixes basic punctuation. Used when no cloud API key
/// is available in Local mode. Cloud correction (OpenAI) is preferred when a
/// key is configured, even alongside local STT.
/// </summary>
public sealed partial class LocalHeuristicCorrector : ITranscriptCorrector
{
    private static readonly string[] Fillers =
    [
        "um", "uh", "er", "ah", "like", "you know", "i mean", "sort of", "kind of",
    ];

    public Task<string> CorrectAsync(
        string transcript,
        CorrectionIntensity intensity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string result = transcript.Trim();
        if (result.Length == 0)
        {
            return Task.FromResult(result);
        }

        result = RemoveFillers(result);
        result = CollapseRepeatedWords(result);
        result = RemoveFalseStarts(result);
        result = NormalizeWhitespace(result);

        if (intensity == CorrectionIntensity.Aggressive)
        {
            result = TrimIncompleteEnding(result);
        }

        result = CapitalizeFirst(result);
        result = EnsureEndingPunctuation(result);

        return Task.FromResult(result);
    }

    private static string RemoveFillers(string text)
    {
        string result = text;
        foreach (string filler in Fillers)
        {
            result = Regex.Replace(
                result,
                $@"\b{Regex.Escape(filler)}\b[,.]?\s*",
                "",
                RegexOptions.IgnoreCase);
        }

        return result;
    }

    private static string CollapseRepeatedWords(string text) =>
        RepeatedWordPattern().Replace(text, "$1");

    private static string RemoveFalseStarts(string text)
    {
        string result = text;
        string[] patterns =
        [
            @"\bno wait\b[,.]?\s*",
            @"\bwait no\b[,.]?\s*",
            @"\bi mean\b[,.]?\s*",
            @"\bactually\b[,.]?\s*",
            @"\bsorry\b[,.]?\s*",
        ];

        foreach (string pattern in patterns)
        {
            result = Regex.Replace(result, pattern, "", RegexOptions.IgnoreCase);
        }

        return result.Trim();
    }

    private static string TrimIncompleteEnding(string text)
    {
        string[] dangling =
        [
            " and", " the", " to", " a", " an", " or", " but", " so", " because",
            " if", " when", " that", " which", " with", " for",
        ];

        string lower = text.ToLowerInvariant();
        foreach (string ending in dangling)
        {
            if (lower.EndsWith(ending, StringComparison.Ordinal))
            {
                return text[..^ending.Length].TrimEnd();
            }
        }

        return text;
    }

    private static string NormalizeWhitespace(string text) =>
        WhitespacePattern().Replace(text.Trim(), " ");

    private static string CapitalizeFirst(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        return char.ToUpper(text[0]) + text[1..];
    }

    private static string EnsureEndingPunctuation(string text)
    {
        if (text.Length < 3)
        {
            return text;
        }

        char last = text[^1];
        if (last is '.' or '!' or '?' or ':' or ';')
        {
            return text;
        }

        return text + ".";
    }

    [GeneratedRegex(@"\b(\w+)\s+\1\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedWordPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
