using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Mocks;

/// <summary>Test double that prefixes corrected text for pipeline assertions.</summary>
public sealed class FakeCorrector : ITranscriptCorrector
{
    public string Prefix { get; init; } = "[fixed] ";

    public Task<string> CorrectAsync(
        string transcript,
        CorrectionIntensity intensity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult($"{Prefix}{transcript}");
}
