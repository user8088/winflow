using NSubstitute;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Local;
using WinFlow.Core.Models;

namespace WinFlow.Core.Tests;

public class DispatchingSttProviderTests
{
    private static readonly CapturedAudio Take = new(
        new byte[24000], 24000, TimeSpan.FromSeconds(1), 0.05f);

    private readonly IStreamingSttProvider _cloudStreaming = Substitute.For<IStreamingSttProvider>();
    private readonly IStreamingSttSession _cloudSession = Substitute.For<IStreamingSttSession>();
    private readonly IBatchSttProvider _cloudBatch = Substitute.For<IBatchSttProvider>();
    private readonly IBatchSttProvider _localBatch = Substitute.For<IBatchSttProvider>();

    public DispatchingSttProviderTests()
    {
        _cloudStreaming.OpenSessionAsync(Arg.Any<CancellationToken>()).Returns(_cloudSession);
        _cloudBatch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>()).Returns("cloud");
        _localBatch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>()).Returns("local");
    }

    [Fact]
    public async Task CloudBackendUsesCloudProviders()
    {
        var controller = new SttModeController(SttMode.Cloud, () => true, () => true);
        var provider = new DispatchingSttProvider(controller, _cloudStreaming, _cloudBatch, _localBatch);

        IStreamingSttSession session = await provider.OpenSessionAsync();
        string transcript = await provider.TranscribeAsync(Take);

        Assert.Same(_cloudSession, session);
        Assert.Equal("cloud", transcript);
        await _localBatch.DidNotReceive().TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocalBackendUsesLocalBatchAndNoOpStreaming()
    {
        var controller = new SttModeController(SttMode.Local, () => true, () => true);
        var provider = new DispatchingSttProvider(controller, _cloudStreaming, _cloudBatch, _localBatch);

        IStreamingSttSession session = await provider.OpenSessionAsync();
        string transcript = await provider.TranscribeAsync(Take);

        // The local streaming session is a no-op whose finish returns "" so
        // the pipeline's race lets the batch (on-device) result win.
        Assert.Empty(await session.FinishAsync());
        Assert.Equal("local", transcript);
        await _cloudStreaming.DidNotReceive().OpenSessionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SwitchingModeMidFlightReRoutes()
    {
        var controller = new SttModeController(SttMode.Cloud, () => true, () => true);
        var provider = new DispatchingSttProvider(controller, _cloudStreaming, _cloudBatch, _localBatch);

        Assert.Equal("cloud", await provider.TranscribeAsync(Take));

        controller.Apply(SttMode.Local);
        Assert.Equal("local", await provider.TranscribeAsync(Take));

        controller.Apply(SttMode.Cloud);
        Assert.Equal("cloud", await provider.TranscribeAsync(Take));
    }

    [Fact]
    public async Task LocalWithoutLocalBatchFallsBackToCloud()
    {
        var controller = new SttModeController(SttMode.Local, () => true, () => true);
        var provider = new DispatchingSttProvider(controller, _cloudStreaming, _cloudBatch, localBatch: null);

        Assert.Equal("cloud", await provider.TranscribeAsync(Take));
    }
}
