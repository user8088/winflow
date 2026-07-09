using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Correction;
using WinFlow.Core.Injection;
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
        DictationPipelineOptions? options = null,
        Action<string>? clipboardFallback = null)
    {
        return new DictationPipeline(
            _hotkeys, _audio, _coordinator,
            streaming ?? _streaming,
            batch ?? _batch,
            _injector,
            correction,
            options,
            // Keep tests off the real Win32 clipboard by default.
            clipboardFallback ?? (_ => { }));
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

    // #34: the empty-string race NullStreamingSession relies on — an empty
    // streaming result must be skipped, not treated as a win.
    [Fact]
    public async Task EmptyStreamingResultFallsBackToBatch()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns(string.Empty);
        _batch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                // Finish well after the (instant) empty streaming result so the
                // race provably evaluates the empty transcript first.
                await Task.Delay(500, callInfo.Arg<CancellationToken>());
                return "batch text";
            });

        using var pipeline = CreatePipeline();
        string? completed = null;
        pipeline.DictationCompleted += text => completed = text;

        await RunFullSessionAsync(pipeline);

        Assert.Equal("batch text", completed);
        await _injector.Received(1).InjectAsync("batch text", Arg.Any<CancellationToken>());
        await WaitUntilAsync(() => SessionDisposeCount() > 0);
        await _session.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task BothPathsEmptyReportsTranscriptionFailed()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns(string.Empty);
        _batch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        using var pipeline = CreatePipeline();
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await RunFullSessionAsync(pipeline);

        Assert.Equal(DictationFailureKind.TranscriptionFailed, failure?.Kind);
        Assert.Equal("Transcription returned no text.", failure?.Message);
        await _injector.DidNotReceive().InjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(RecordingState.Idle, pipeline.State);
    }

    // #28: a failing capture stop must still release the streaming session.
    [Fact]
    public async Task SessionIsDisposedWhenAudioStopFails()
    {
        _audio.StopAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("device unplugged"));

        using var pipeline = CreatePipeline();
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await RunFullSessionAsync(pipeline);

        Assert.Equal(DictationFailureKind.CaptureFailed, failure?.Kind);
        await WaitUntilAsync(() => SessionDisposeCount() > 0);
        await _session.Received(1).DisposeAsync();
    }

    // #66: the no-speech discard path must observe a still-running forwarding
    // task so a late session-open fault does not become unobserved.
    [Fact]
    public async Task NoSpeechPathObservesForwardingTaskFault()
    {
        var sessionReady = new TaskCompletionSource<IStreamingSttSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _streaming.OpenSessionAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => sessionReady.Task);
        _audio.StopAsync(Arg.Any<CancellationToken>()).Returns(SilentTake);

        var unobserved = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            unobserved.TrySetResult(e.Exception.GetBaseException());
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        try
        {
            using var pipeline = CreatePipeline();
            DictationFailure? failure = null;
            pipeline.DictationFailed += f => failure = f;

            await RunFullSessionAsync(pipeline);

            Assert.Equal(DictationFailureKind.NoSpeech, failure?.Kind);

            sessionReady.SetException(new InvalidOperationException("streaming open failed"));
            await Task.Delay(200);

            Assert.False(unobserved.Task.IsCompleted);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        }
    }

    // #29: batch winning while the streaming session is still connecting must
    // not leak the session once the open eventually completes.
    [Fact]
    public async Task SessionIsDisposedWhenBatchWinsBeforeSessionOpens()
    {
        var sessionReady = new TaskCompletionSource<IStreamingSttSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _streaming.OpenSessionAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => sessionReady.Task);
        _batch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>())
            .Returns("batch wins");

        using var pipeline = CreatePipeline();
        string? completed = null;
        pipeline.DictationCompleted += text => completed = text;

        await RunFullSessionAsync(pipeline);
        Assert.Equal("batch wins", completed);

        // The streaming connect only finishes after the whole take is over.
        sessionReady.SetResult(_session);

        await WaitUntilAsync(() => SessionDisposeCount() > 0);
        await _session.Received(1).DisposeAsync();
    }

    // #30: the fallback message must only advertise the clipboard when the
    // fallback write actually succeeded.
    [Fact]
    public async Task InjectionFallbackSuccessAdvertisesClipboard()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("precious words");
        _injector.InjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("paste blocked"));

        string? savedToClipboard = null;
        using var pipeline = CreatePipeline(clipboardFallback: text => savedToClipboard = text);
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await RunFullSessionAsync(pipeline);

        Assert.Equal("precious words", savedToClipboard);
        Assert.Equal(DictationFailureKind.InjectionFailed, failure?.Kind);
        Assert.Contains("Text is on the clipboard", failure?.Message);
        Assert.Equal("precious words", failure?.Transcript);
    }

    [Fact]
    public async Task InjectionFallbackFailureIsReportedHonestly()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("precious words");
        _injector.InjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("paste blocked"));

        using var pipeline = CreatePipeline(
            clipboardFallback: _ => throw new InvalidOperationException("clipboard held by another app"));
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await RunFullSessionAsync(pipeline);

        Assert.Equal(DictationFailureKind.InjectionFailed, failure?.Kind);
        Assert.Contains("NOT on the clipboard", failure?.Message);
        Assert.DoesNotContain("press Ctrl+V", failure?.Message);
        Assert.Equal("precious words", failure?.Transcript);
        Assert.Equal(RecordingState.Idle, pipeline.State);
    }

    // #20: partial keystroke injection must not trigger clipboard fallback.
    [Fact]
    public async Task PartialInjectionFailureSkipsClipboardFallback()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("hello world");
        _injector.InjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PartialTextInjectionException(5, 5));

        bool fallbackCalled = false;
        using var pipeline = CreatePipeline(clipboardFallback: _ => fallbackCalled = true);
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await RunFullSessionAsync(pipeline);

        Assert.False(fallbackCalled);
        Assert.Equal(DictationFailureKind.InjectionFailed, failure?.Kind);
        Assert.Contains("do not paste", failure?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Text is on the clipboard", failure?.Message);
        Assert.Equal("hello world", failure?.Transcript);
    }

    [Fact]
    public async Task ElevatedTargetMessageReachesUserVerbatim()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("admin words");
        _injector.InjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(ElevatedTargetDetector.BlockedMessage));

        bool fallbackCalled = false;
        using var pipeline = CreatePipeline(clipboardFallback: _ => fallbackCalled = true);
        DictationFailure? failure = null;
        pipeline.DictationFailed += f => failure = f;

        await RunFullSessionAsync(pipeline);

        Assert.Equal(ElevatedTargetDetector.BlockedMessage, failure?.Message);
        Assert.Equal("admin words", failure?.Transcript);
        // The injector already put the text on the clipboard before throwing.
        Assert.False(fallbackCalled);
    }

    // #41: re-entrancy guards.
    [Fact]
    public async Task AutoRepeatPressWhileRecordingIsIgnored()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("hello");

        using var pipeline = CreatePipeline();
        var failures = new List<DictationFailure>();
        pipeline.DictationFailed += failures.Add;
        string? completed = null;
        pipeline.DictationCompleted += text => completed = text;

        await pipeline.ProcessAsync(HotkeyEvent.Pressed());
        await pipeline.ProcessAsync(HotkeyEvent.Pressed()); // key auto-repeat
        await pipeline.ProcessAsync(HotkeyEvent.Released());

        await _audio.Received(1).StartAsync(Arg.Any<CancellationToken>());
        await _streaming.Received(1).OpenSessionAsync(Arg.Any<CancellationToken>());
        Assert.Empty(failures);
        Assert.Equal("hello", completed);
    }

    [Fact]
    public async Task StrayReleaseWithoutPressIsIgnored()
    {
        using var pipeline = CreatePipeline();
        var failures = new List<DictationFailure>();
        pipeline.DictationFailed += failures.Add;

        await pipeline.ProcessAsync(HotkeyEvent.Released());

        await _audio.DidNotReceive().StopAsync(Arg.Any<CancellationToken>());
        Assert.Empty(failures);
        Assert.Equal(RecordingState.Idle, pipeline.State);
    }

    [Fact]
    public async Task DoubleReleaseDoesNotProcessTwice()
    {
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("once");

        using var pipeline = CreatePipeline();
        int completions = 0;
        pipeline.DictationCompleted += _ => completions++;

        await pipeline.ProcessAsync(HotkeyEvent.Pressed());
        await pipeline.ProcessAsync(HotkeyEvent.Released());
        await pipeline.ProcessAsync(HotkeyEvent.Released()); // stray double-release

        await _audio.Received(1).StopAsync(Arg.Any<CancellationToken>());
        await _injector.Received(1).InjectAsync("once", Arg.Any<CancellationToken>());
        Assert.Equal(1, completions);
    }

    // #27 + #41(c): a press while the previous take is transcribing must be
    // rejected immediately with feedback, not queued to start recording late.
    [Fact]
    public async Task PressDuringProcessingIsRejectedWithBusyFeedback()
    {
        var finishGate = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns(callInfo => finishGate.Task);
        var batchGate = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _batch.TranscribeAsync(Arg.Any<CapturedAudio>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => batchGate.Task);

        using var pipeline = CreatePipeline();
        var failures = new List<DictationFailure>();
        pipeline.DictationFailed += failures.Add;
        string? completed = null;
        pipeline.DictationCompleted += text => completed = text;

        await pipeline.ProcessAsync(HotkeyEvent.Pressed());
        Task releaseTask = pipeline.ProcessAsync(HotkeyEvent.Released());
        Assert.Equal(RecordingState.Processing, pipeline.State);

        // Second push-to-talk while the first take is still transcribing.
        await pipeline.ProcessAsync(HotkeyEvent.Pressed());

        DictationFailure busy = Assert.Single(failures);
        Assert.Equal(DictationFailureKind.CaptureFailed, busy.Kind);
        Assert.Contains("previous dictation", busy.Message);
        // Recording was not started a second time (neither now nor queued).
        await _audio.Received(1).StartAsync(Arg.Any<CancellationToken>());

        finishGate.SetResult("first take");
        await releaseTask;

        Assert.Equal("first take", completed);
        Assert.Single(failures);
        Assert.Equal(RecordingState.Idle, pipeline.State);
    }

    [Fact]
    public async Task AutoStopsWhenMaxRecordingDurationExceeded()
    {
        var stopGate = new TaskCompletionSource<CapturedAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        _audio.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _audio.StopAsync(Arg.Any<CancellationToken>()).Returns(stopGate.Task);
        _session.FinishAsync(Arg.Any<CancellationToken>()).Returns("capped take");

        var options = new DictationPipelineOptions
        {
            MaxRecordingDuration = TimeSpan.FromMilliseconds(50),
        };

        using var pipeline = CreatePipeline(options: options);

        TimeSpan? cappedAt = null;
        pipeline.RecordingDurationCapped += limit => cappedAt = limit;
        string? completed = null;
        pipeline.DictationCompleted += text => completed = text;

        await pipeline.ProcessAsync(HotkeyEvent.Pressed());
        Assert.Equal(RecordingState.Recording, pipeline.State);

        await WaitUntilAsync(() => cappedAt.HasValue);

        stopGate.SetResult(GoodTake);
        await WaitUntilAsync(() => pipeline.State == RecordingState.Idle);

        Assert.Equal(TimeSpan.FromMilliseconds(50), cappedAt);
        Assert.Equal("capped take", completed);
        await _audio.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    private int SessionDisposeCount()
        => _session.ReceivedCalls().Count(
            call => call.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    private sealed class HttpRequestExceptionFake(string message) : Exception(message);
}
