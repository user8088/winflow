namespace WinFlow.Core.Abstractions;

/// <summary>Rewrites a raw speech transcript into polished, grammatical text.</summary>
public interface ITranscriptCorrector
{
    Task<string> CorrectAsync(
        string transcript,
        CorrectionIntensity intensity,
        CancellationToken cancellationToken = default);
}

/// <summary>How strongly the corrector should rewrite incomplete or broken dictation.</summary>
public enum CorrectionIntensity
{
    Standard,
    Aggressive,
}
