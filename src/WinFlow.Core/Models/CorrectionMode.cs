namespace WinFlow.Core.Models;

/// <summary>How aggressively dictated transcripts are cleaned up before injection.</summary>
public enum CorrectionMode
{
    /// <summary>Inject the raw STT transcript unchanged.</summary>
    Off,

    /// <summary>Fix transcripts that look messy or incomplete; skip already-clean takes.</summary>
    AutoCorrect,

    /// <summary>Always run correction, including stronger completion for broken English.</summary>
    Aggressive,
}
