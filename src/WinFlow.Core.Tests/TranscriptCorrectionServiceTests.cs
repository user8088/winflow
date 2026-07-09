using WinFlow.Core.Abstractions;
using WinFlow.Core.Correction;
using WinFlow.Core.Mocks;
using WinFlow.Core.Models;

namespace WinFlow.Core.Tests;

public class TranscriptCorrectionServiceTests
{
    [Fact]
    public async Task OffModeReturnsRaw()
    {
        var service = new TranscriptCorrectionService(
            () => CorrectionMode.Off,
            new FakeCorrector());

        string result = await service.ProcessAsync("um hello world");

        Assert.Equal("um hello world", result);
    }

    [Fact]
    public async Task AutoModeSkipsCleanTranscript()
    {
        var service = new TranscriptCorrectionService(
            () => CorrectionMode.AutoCorrect,
            new FakeCorrector());

        string result = await service.ProcessAsync("Hello world.");

        Assert.Equal("Hello world.", result);
    }

    [Fact]
    public async Task AutoModeCorrectsMessyTranscript()
    {
        var service = new TranscriptCorrectionService(
            () => CorrectionMode.AutoCorrect,
            new FakeCorrector());

        string result = await service.ProcessAsync("um I need to uh send the report");

        Assert.Equal("[fixed] um I need to uh send the report", result);
    }

    [Fact]
    public async Task AggressiveModeAlwaysCorrects()
    {
        var service = new TranscriptCorrectionService(
            () => CorrectionMode.Aggressive,
            new FakeCorrector());

        string result = await service.ProcessAsync("Hello world.");

        Assert.Equal("[fixed] Hello world.", result);
    }

    [Fact]
    public async Task FallsBackToRawOnCorrectorFailure()
    {
        var failing = new FailingCorrector();
        var service = new TranscriptCorrectionService(
            () => CorrectionMode.Aggressive,
            failing);

        string result = await service.ProcessAsync("broken text um");

        Assert.Equal("broken text um", result);
    }

    [Fact]
    public async Task FallsBackToRawOnDrasticTruncation()
    {
        var service = new TranscriptCorrectionService(
            () => CorrectionMode.Aggressive,
            new TruncatingCorrector());

        string result = await service.ProcessAsync(
            "um so I was thinking we should probably reschedule the meeting");

        Assert.Equal(
            "um so I was thinking we should probably reschedule the meeting",
            result);
    }

    [Fact]
    public async Task QuotedOutputIsUnwrapped()
    {
        var service = new TranscriptCorrectionService(
            () => CorrectionMode.Aggressive,
            new FixedOutputCorrector("\"I was thinking we should reschedule.\""));

        string result = await service.ProcessAsync(
            "um I was thinking we should reschedule");

        Assert.Equal("I was thinking we should reschedule.", result);
    }

    [Fact]
    public async Task FencedOutputIsUnwrapped()
    {
        var service = new TranscriptCorrectionService(
            () => CorrectionMode.Aggressive,
            new FixedOutputCorrector("```\nI was thinking we should reschedule.\n```"));

        string result = await service.ProcessAsync(
            "um I was thinking we should reschedule");

        Assert.Equal("I was thinking we should reschedule.", result);
    }

    [Fact]
    public async Task PreambleOutputFallsBackToRaw()
    {
        var service = new TranscriptCorrectionService(
            () => CorrectionMode.Aggressive,
            new FixedOutputCorrector(
                "Here is the corrected text: I was thinking we should reschedule."));

        string result = await service.ProcessAsync(
            "um I was thinking we should reschedule");

        Assert.Equal("um I was thinking we should reschedule", result);
    }

    private sealed class FixedOutputCorrector(string output) : ITranscriptCorrector
    {
        public Task<string> CorrectAsync(
            string transcript,
            CorrectionIntensity intensity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(output);
    }

    private sealed class FailingCorrector : ITranscriptCorrector
    {
        public Task<string> CorrectAsync(
            string transcript,
            CorrectionIntensity intensity,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("correction failed");
    }

    private sealed class TruncatingCorrector : ITranscriptCorrector
    {
        public Task<string> CorrectAsync(
            string transcript,
            CorrectionIntensity intensity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("The");
    }
}
