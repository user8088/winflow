namespace WinFlow.Core.Models;

/// <summary>How aggressively dictated transcripts are cleaned up before injection.</summary>
public enum CorrectionMode
{
    /// <summary>Inject the transcript verbatim; no correction pass.</summary>
    Off,

    /// <summary>Correct only takes the heuristic gate flags as messy (fillers, self-corrections).</summary>
    AutoCorrect,

    /// <summary>Correct every take, rewriting more freely for grammar and fluency.</summary>
    Aggressive,
}
