using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Correction;

/// <summary>Returns the transcript unchanged. Used when correction is unavailable.</summary>
public sealed class PassthroughCorrector : ITranscriptCorrector
{
    public static readonly PassthroughCorrector Instance = new();

    public Task<string> CorrectAsync(
        string transcript,
        CorrectionIntensity intensity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(transcript);
}
