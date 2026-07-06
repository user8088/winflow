using WinFlow.Core.Abstractions;
using WinFlow.Core.Correction;
using WinFlow.Core.Local;
using WinFlow.Core.Mocks;
using WinFlow.Core.Models;
using WinFlow.Core.Services;

namespace WinFlow.Core.Tests;

public class DispatchingCorrectorTests
{
    [Fact]
    public async Task RoutesToCloudWhenCloudBackendActive()
    {
        var controller = new SttModeController(SttMode.Cloud, () => true, () => true);
        var cloud = new TrackingCorrector("cloud");
        var local = new TrackingCorrector("local");
        var corrector = new DispatchingCorrector(controller, cloud, local);

        string result = await corrector.CorrectAsync("test", CorrectionIntensity.Standard);

        Assert.Equal("cloud:test", result);
    }

    [Fact]
    public async Task RoutesToLocalWhenLocalBackendActive()
    {
        var controller = new SttModeController(SttMode.Local, () => true, () => true);
        var cloud = new TrackingCorrector("cloud");
        var local = new TrackingCorrector("local");
        var corrector = new DispatchingCorrector(controller, cloud, local);

        string result = await corrector.CorrectAsync("test", CorrectionIntensity.Standard);

        Assert.Equal("local:test", result);
    }

    private sealed class TrackingCorrector(string tag) : Abstractions.ITranscriptCorrector
    {
        public Task<string> CorrectAsync(
            string transcript,
            Abstractions.CorrectionIntensity intensity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult($"{tag}:{transcript}");
    }
}
