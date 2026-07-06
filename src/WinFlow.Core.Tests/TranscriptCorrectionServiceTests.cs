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

    private sealed class FailingCorrector : ITranscriptCorrector
    {
        public Task<string> CorrectAsync(
            string transcript,
            CorrectionIntensity intensity,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("correction failed");
    }
}
