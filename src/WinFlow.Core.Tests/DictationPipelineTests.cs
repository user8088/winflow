using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Correction;
using WinFlow.Core.Mocks;
using WinFlow.Core.Models;
using WinFlow.Core.Services;

namespace WinFlow.Core.Tests;

public class DictationPipelineTests
{
    private static readonly CapturedAudio GoodTake = new(
        new byte[24000], 24000, TimeSpan.FromSeconds(1), 0.05f);

    private static readonly CapturedAudio SilentTake = new(
        new byte[24000], 24000, TimeSpan.FromSeconds(1), 0.0001f);

    private static readonly CapturedAudio ShortTake = new(
        new byte[480], 24000, TimeSpan.FromMilliseconds(10), 0.05f);

    private readonly IHotkeyProvider _hotkeys = Substitute.For<IHotkeyProvider>();
    private readonly IAudioProvider _audio = Substitute.For<IAudioProvider>();
    private readonly RecordingCoordinator _coordinator = new();
    private readonly IStreamingSttProvider _streaming = Substitute.For<IStreamingSttProvider>();
    private readonly IStreamingSttSession _session = Substitute.For<IStreamingSttSession>();
    private readonly IBatchSttProvider _batch = Substitute.For<IBatchSttProvider>();
    private readonly ITextInjector _injector = Substitute.For<ITextInjector>();

    public DictationPipelineTests()
    {
        _streaming.OpenSessionAsync(Arg.Any<CancellationToken>()).Returns(_session);
        _audio.StopAsync(Arg.Any<CancellationToken>()).Returns(GoodTake);
    }

    private DictationPipeline CreatePipeline(
        IStreamingSttProvider? streaming = null,
        IBatchSttProvider? batch = null,
        TranscriptCorrectionService? correction = null,
        DictationPipelineOptions? options = null)
    {
        return new DictationPipeline(
            _hotkeys, _audio, _coordinator,
            streaming ?? _streaming,
            batch ?? _batch,
            _injector,
            correction,
            options);
    }

    private async Task RunFullSessionAsync(DictationPipeline pipeline)
    {
        await pipeline.ProcessAsync(HotkeyEvent.Pressed());
        await pipeline.ProcessAsync(HotkeyEvent.Released());
    }

    [Fact]
    public async Task StreamingTranscriptIsInjected()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("hello world");
        _batch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await Task.Delay(5000, callInfo.Arg<CancellationToken>());
                return "batch should lose";
            });

        using var pipeline = CreatePipeline();
        string? completed = null;
        pipeline.DictationCompleted += text => completed = text;

        await RunFullSessionAsync(pipeline);

        Assert.Equal("hello world", completed);
        await _injector.Received(1).InjectAsync("hello world", Arg.Any<CancellationToken>());
        Assert.Equal(RecordingState.Idle, pipeline.State);
    }

    [Fact]
    public async Task BatchWinsWhenStreamingFails()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("socket died"));
        _batch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>())
            .Returns("batch to the rescue");

        using var pipeline = CreatePipeline();
        string? completed = null;
        pipeline.DictationCompleted += text => completed = text;

        await RunFullSessionAsync(pipeline);

        Assert.Equal("batch to the rescue", completed);
    }

    [Fact]
    public async Task BatchWinsWhenSessionOpenFails()
    {
        _streaming.OpenSessionAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no network"));
        _batch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>())
            .Returns("batch survives");

        using var pipeline = CreatePipeline();
        string? completed = null;
        pipeline.DictationCompleted += text => completed = text;

        await RunFullSessionAsync(pipeline);

        Assert.Equal("batch survives", completed);
    }

    [Fact]
    public async Task ReportsFailureWhenAllPathsFail()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("stream broke"));
        _batch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestExceptionFake("401 unauthorized"));

        using var pipeline = CreatePipeline();
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await RunFullSessionAsync(pipeline);

        Assert.NotNull(failure);
        Assert.Equal(DictationFailureKind.TranscriptionFailed, failure.Kind);
        await _injector.DidNotReceive().InjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(RecordingState.Idle, pipeline.State);
    }

    [Fact]
    public async Task SilentTakeIsGatedWithoutTranscription()
    {
        _audio.StopAsync(Arg.Any<CancellationToken>()).Returns(SilentTake);

        using var pipeline = CreatePipeline();
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await RunFullSessionAsync(pipeline);

        Assert.Equal(DictationFailureKind.NoSpeech, failure?.Kind);
        await _batch.DidNotReceive().TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>());
        await _injector.DidNotReceive().InjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShortTapIsGatedWithoutTranscription()
    {
        _audio.StopAsync(Arg.Any<CancellationToken>()).Returns(ShortTake);

        using var pipeline = CreatePipeline();
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await RunFullSessionAsync(pipeline);

        Assert.Equal(DictationFailureKind.NoSpeech, failure?.Kind);
        await _batch.DidNotReceive().TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InjectionFailureKeepsTranscript()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("precious words");
        _injector.InjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("clipboard locked"));

        using var pipeline = CreatePipeline();
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await RunFullSessionAsync(pipeline);

        Assert.Equal(DictationFailureKind.InjectionFailed, failure?.Kind);
        Assert.Equal("precious words", failure?.Transcript);
        Assert.Equal(RecordingState.Idle, pipeline.State);
    }

    [Fact]
    public async Task WorksWithBatchOnlyProvider()
    {
        _batch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>())
            .Returns("batch only");

        using var pipeline = new DictationPipeline(
            _hotkeys, _audio, _coordinator, streaming: null, _batch, _injector, correction: null);
        string? completed = null;
        pipeline.DictationCompleted += text => completed = text;

        await RunFullSessionAsync(pipeline);

        Assert.Equal("batch only", completed);
    }

    [Fact]
    public async Task CaptureStartFailureReportsAndResets()
    {
        _audio.StartAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no microphone"));

        using var pipeline = CreatePipeline();
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await pipeline.ProcessAsync(HotkeyEvent.Pressed());

        Assert.Equal(DictationFailureKind.CaptureFailed, failure?.Kind);
        Assert.Equal(RecordingState.Idle, pipeline.State);
    }

    [Fact]
    public async Task SessionIsDisposedAfterStreamingWin()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("done");

        using var pipeline = CreatePipeline();
        await RunFullSessionAsync(pipeline);

        await _session.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task CorrectedTranscriptIsInjected()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("um hello world");
        var correction = new TranscriptCorrectionService(
            () => CorrectionMode.Aggressive,
            new FakeCorrector());

        using var pipeline = CreatePipeline(correction: correction);
        string? completed = null;
        pipeline.DictationCompleted += text => completed = text;

        await RunFullSessionAsync(pipeline);

        Assert.Equal("[fixed] um hello world", completed);
        await _injector.Received(1).InjectAsync("[fixed] um hello world", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequiresAtLeastOneProvider()
    {
        Assert.Throws<ArgumentException>(() => new DictationPipeline(
            _hotkeys, _audio, _coordinator, streaming: null, batch: null, _injector, correction: null));
        await Task.CompletedTask;
    }

    private sealed class HttpRequestExceptionFake(string message) : Exception(message);
}
