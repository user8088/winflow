using System.Threading.Channels;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Audio;
using WinFlow.Core.Correction;
using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

public sealed class DictationPipelineOptions
{
    /// <summary>Takes shorter than this never leave the machine.</summary>
    public TimeSpan MinimumDuration { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Takes whose peak RMS never exceeds this are treated as silence.</summary>
    public float SilencePeakThreshold { get; init; } = 0.0008f;

    /// <summary>Deadline for the streaming path after key release.</summary>
    public TimeSpan StreamingFinishTimeout { get; init; } = TimeSpan.FromSeconds(7);

    /// <summary>Deadline for the batch fallback after key release.</summary>
    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Longest allowed hold; recording auto-stops when exceeded.</summary>
    public TimeSpan MaxRecordingDuration { get; init; } = WasapiAudioProvider.DefaultMaxRecordingDuration;
}

/// <summary>
/// Orchestrates the full dictation flow: hotkey press starts capture and
/// opens a streaming transcription session in parallel; audio chunks are
/// forwarded while the user speaks; on release, the streaming finish races
/// a batch transcription of the complete take, and the winning transcript
/// is injected at the cursor.
/// </summary>
public sealed class DictationPipeline : IDisposable
{
    private readonly IHotkeyProvider _hotkeys;
    private readonly IAudioProvider _audio;
    private readonly RecordingCoordinator _coordinator;
    private readonly IStreamingSttProvider? _streaming;
    private readonly IBatchSttProvider? _batch;
    private readonly ITextInjector _injector;
    private readonly TranscriptCorrectionService? _correction;
    private readonly DictationPipelineOptions _options;
    private readonly Action<string> _clipboardFallback;

    // Hotkey events arrive sequentially, but a press can follow a release
    // faster than the async work completes; serialize session handling.
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private Channel<byte[]>? _chunkChannel;
    private Task<IStreamingSttSession>? _sessionTask;
    private Task? _forwardingTask;
    private CancellationTokenSource? _recordingLimitCts;

    /// <summary>Latest chunk RMS, for HUD metering. Raised on the capture thread.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Raised after the full take passed the silence gate (for optional WAV persistence).</summary>
    public event Action<CapturedAudio>? AudioCaptured;

    public event Action<string>? DictationCompleted;

    public event Action<DictationFailure>? DictationFailed;

    /// <summary>Raised when hold exceeded <see cref="DictationPipelineOptions.MaxRecordingDuration"/>.</summary>
    public event Action<TimeSpan>? RecordingDurationCapped;

    public DictationPipeline(
        IHotkeyProvider hotkeys,
        IAudioProvider audio,
        RecordingCoordinator coordinator,
        IStreamingSttProvider? streaming,
        IBatchSttProvider? batch,
        ITextInjector injector,
        TranscriptCorrectionService? correction = null,
        DictationPipelineOptions? options = null,
        Action<string>? clipboardFallback = null)
    {
        if (streaming is null && batch is null)
        {
            throw new ArgumentException("At least one transcription provider is required.");
        }

        _hotkeys = hotkeys;
        _audio = audio;
        _coordinator = coordinator;
        _streaming = streaming;
        _batch = batch;
        _injector = injector;
        _correction = correction;
        _options = options ?? new DictationPipelineOptions();
        _clipboardFallback = clipboardFallback ?? (text => _ = Injection.ClipboardHelper.SetText(text));

        _hotkeys.HotkeyChanged += OnHotkeyChanged;
        _audio.ChunkAvailable += OnChunkAvailable;
    }

    public RecordingState State => _coordinator.State;

    private void OnChunkAvailable(AudioChunk chunk)
    {
        LevelChanged?.Invoke(chunk.Rms);
        _chunkChannel?.Writer.TryWrite(chunk.Pcm16);
    }

    private async void OnHotkeyChanged(HotkeyEvent hotkeyEvent)
    {
        try
        {
            await ProcessAsync(hotkeyEvent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DictationFailed?.Invoke(new DictationFailure(
                DictationFailureKind.CaptureFailed, exception.GetBaseException().Message));
            _coordinator.Reset();
        }
    }

    /// <summary>Exposed for tests; production traffic arrives via the hotkey event.</summary>
    public async Task ProcessAsync(HotkeyEvent hotkeyEvent)
    {
        // A press landing while the previous take is still transcribing must
        // not queue behind the session lock: recording would only start once
        // the lock frees, seconds after the user began speaking, and the
        // utterance would be silently lost. Reject it with immediate feedback
        // instead. A press during Recording still takes the lock (it is free
        // then) so auto-repeat remains a silent no-op via the state guard.
        if (hotkeyEvent.Kind == HotkeyEventKind.Pressed
            && _coordinator.State is RecordingState.Processing)
        {
            DictationFailed?.Invoke(new DictationFailure(
                DictationFailureKind.CaptureFailed,
                "Still finishing the previous dictation — try again in a moment."));
            return;
        }

        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (hotkeyEvent.Kind == HotkeyEventKind.Pressed)
            {
                await BeginAsync().ConfigureAwait(false);
            }
            else
            {
                await CompleteAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task BeginAsync()
    {
        if (!_coordinator.TryTransition(RecordingState.Idle, RecordingState.Recording))
        {
            return;
        }

        try
        {
            await _audio.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _coordinator.TryTransition(RecordingState.Recording, RecordingState.Idle);
            DictationFailed?.Invoke(new DictationFailure(
                DictationFailureKind.CaptureFailed, exception.GetBaseException().Message));
            return;
        }

        if (_streaming is not null)
        {
            // Open the session and forward chunks concurrently with capture;
            // chunks buffered in the channel flush once the session is ready.
            var channel = Channel.CreateUnbounded<byte[]>(
                new UnboundedChannelOptions { SingleReader = true });
            _chunkChannel = channel;
            Task<IStreamingSttSession> sessionTask = Task.Run(() => _streaming.OpenSessionAsync());
            _sessionTask = sessionTask;
            _forwardingTask = Task.Run(() => ForwardChunksAsync(channel.Reader, sessionTask));
        }

        StartRecordingLimitTimer();
    }

    private async Task CompleteAsync()
    {
        CancelRecordingLimitTimer();

        if (!_coordinator.TryTransition(RecordingState.Recording, RecordingState.Processing))
        {
            return;
        }

        Channel<byte[]>? channel = _chunkChannel;
        Task<IStreamingSttSession>? sessionTask = _sessionTask;
        Task? forwardingTask = _forwardingTask;
        _chunkChannel = null;
        _sessionTask = null;
        _forwardingTask = null;

        try
        {
            CapturedAudio captured;
            try
            {
                captured = await _audio.StopAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The streaming session opened on key-down must not leak just
                // because capture teardown failed.
                DiscardStreamingInBackground(sessionTask, forwardingTask);
                DictationFailed?.Invoke(new DictationFailure(
                    DictationFailureKind.CaptureFailed, exception.GetBaseException().Message));
                return;
            }
            finally
            {
                channel?.Writer.TryComplete();
            }

            if (captured.Duration < _options.MinimumDuration
                || captured.PeakRms < _options.SilencePeakThreshold)
            {
                DiscardStreamingInBackground(sessionTask, forwardingTask);
                DictationFailed?.Invoke(new DictationFailure(
                    DictationFailureKind.NoSpeech, "No speech detected."));
                return;
            }

            AudioCaptured?.Invoke(captured);

            string? transcript = await TranscribeAsync(captured, sessionTask, forwardingTask)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                return; // TranscribeAsync already reported the failure
            }

            string finalText = _correction is not null
                ? await _correction.ProcessAsync(transcript).ConfigureAwait(false)
                : transcript;

            try
            {
                await _injector.InjectAsync(finalText).ConfigureAwait(false);
                DictationCompleted?.Invoke(finalText);
            }
            catch (Exception exception)
            {
                string reason = exception.GetBaseException().Message;
                string message;

                if (reason == Injection.ElevatedTargetDetector.BlockedMessage)
                {
                    // The injector already placed the transcript on the
                    // clipboard before throwing, and its message explains the
                    // elevation trap; pass it through verbatim.
                    message = reason;
                }
                else if (exception.GetBaseException() is Injection.PartialTextInjectionException)
                {
                    // Some text was already typed — clipboard paste would
                    // duplicate the partial transcript in the field.
                    message = $"Couldn't finish typing ({reason}). "
                        + "The partial transcript is already in the field — do not paste from the clipboard.";
                }
                else
                {
                    // Leave the transcript on the clipboard so nothing is
                    // lost — but only claim it is there if that write worked.
                    try
                    {
                        _clipboardFallback(finalText);
                        message = $"Couldn't paste ({reason}). Text is on the clipboard — press Ctrl+V.";
                    }
                    catch (Exception clipboardException)
                    {
                        message = $"Couldn't paste ({reason}), and saving to the clipboard also failed "
                            + $"({clipboardException.GetBaseException().Message}). "
                            + "The text is NOT on the clipboard.";
                    }
                }

                DictationFailed?.Invoke(new DictationFailure(
                    DictationFailureKind.InjectionFailed, message, finalText));
            }
        }
        finally
        {
            _coordinator.TryTransition(RecordingState.Processing, RecordingState.Idle);
        }
    }

    /// <summary>
    /// Races the streaming finish against a batch transcription of the full
    /// take. First non-empty transcript wins; the loser is cancelled.
    /// Reports a failure and returns null if every path fails.
    /// </summary>
    private async Task<string?> TranscribeAsync(
        CapturedAudio captured,
        Task<IStreamingSttSession>? sessionTask,
        Task? forwardingTask)
    {
        using var raceCancellation = new CancellationTokenSource();
        var racers = new List<Task<string>>(2);

        if (sessionTask is not null)
        {
            racers.Add(FinishStreamingAsync(sessionTask, forwardingTask, raceCancellation.Token));
        }

        CancellationTokenSource? batchTimeout = null;
        if (_batch is not null)
        {
            batchTimeout = CancellationTokenSource.CreateLinkedTokenSource(raceCancellation.Token);
            batchTimeout.CancelAfter(_options.BatchTimeout);
            racers.Add(_batch.TranscribeAsync(captured, batchTimeout.Token));
        }

        try
        {
            var pending = new List<Task<string>>(racers);
            Exception? lastError = null;

            while (pending.Count > 0)
            {
                Task<string> finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);

                try
                {
                    string transcript = await finished.ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        raceCancellation.Cancel();
                        ObserveInBackground(pending);
                        return transcript.Trim();
                    }
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }
            }

            DictationFailed?.Invoke(new DictationFailure(
                DictationFailureKind.TranscriptionFailed,
                lastError?.GetBaseException().Message ?? "Transcription returned no text."));
            return null;
        }
        finally
        {
            batchTimeout?.Dispose();
        }
    }

    private async Task<string> FinishStreamingAsync(
        Task<IStreamingSttSession> sessionTask,
        Task? forwardingTask,
        CancellationToken cancellationToken)
    {
        // Set once the session has been captured; from that point the inner
        // finally owns disposal. Before it, a cancellation (batch racer won)
        // or a forwarding fault would otherwise abandon the session the
        // key-down opened, leaking the WebSocket and never re-arming the
        // warm-connection reuse that Session.DisposeAsync provides.
        bool sessionCaptured = false;

        try
        {
            // Ensure every captured chunk reached the server before committing.
            if (forwardingTask is not null)
            {
                await forwardingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            IStreamingSttSession session = await sessionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            sessionCaptured = true;
            try
            {
                return await session
                    .FinishAsync(cancellationToken)
                    .WaitAsync(_options.StreamingFinishTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch when (!sessionCaptured)
        {
            DiscardSessionInBackground(sessionTask);
            throw;
        }
    }

    private static async Task ForwardChunksAsync(
        ChannelReader<byte[]> chunks,
        Task<IStreamingSttSession> sessionTask)
    {
        IStreamingSttSession session = await sessionTask.ConfigureAwait(false);
        await foreach (byte[] chunk in chunks.ReadAllAsync().ConfigureAwait(false))
        {
            await session.SendAudioAsync(chunk).ConfigureAwait(false);
        }
    }

    private static void DiscardStreamingInBackground(
        Task<IStreamingSttSession>? sessionTask,
        Task? forwardingTask)
    {
        if (forwardingTask is not null)
        {
            ObserveInBackground(new[] { forwardingTask });
        }

        DiscardSessionInBackground(sessionTask);
    }

    private static void DiscardSessionInBackground(Task<IStreamingSttSession>? sessionTask)
    {
        if (sessionTask is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                IStreamingSttSession session = await sessionTask.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        });
    }

    /// <summary>Prevents unobserved-task exceptions from losing racers.</summary>
    private static void ObserveInBackground(IEnumerable<Task> tasks)
    {
        foreach (Task task in tasks)
        {
            _ = task.ContinueWith(
                t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private void StartRecordingLimitTimer()
    {
        CancelRecordingLimitTimer();

        _recordingLimitCts = new CancellationTokenSource();
        CancellationToken token = _recordingLimitCts.Token;
        TimeSpan limit = _options.MaxRecordingDuration;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(limit, token).ConfigureAwait(false);
                await AutoStopForRecordingLimitAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void CancelRecordingLimitTimer()
    {
        if (_recordingLimitCts is null)
        {
            return;
        }

        _recordingLimitCts.Cancel();
        _recordingLimitCts.Dispose();
        _recordingLimitCts = null;
    }

    private async Task AutoStopForRecordingLimitAsync()
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_coordinator.State != RecordingState.Recording)
            {
                return;
            }

            RecordingDurationCapped?.Invoke(_options.MaxRecordingDuration);
            await CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public void Dispose()
    {
        CancelRecordingLimitTimer();
        _hotkeys.HotkeyChanged -= OnHotkeyChanged;
        _audio.ChunkAvailable -= OnChunkAvailable;
        _sessionLock.Dispose();
    }
}
