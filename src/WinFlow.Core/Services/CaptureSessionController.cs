using WinFlow.Core.Abstractions;
using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

/// <summary>
/// Orchestrates the M0 capture flow: hotkey press starts audio capture,
/// hotkey release stops it and publishes the completed take. This is the
/// seed of the full DictationPipeline — transcription and injection will
/// slot in between capture completion and <see cref="CaptureCompleted"/>.
/// </summary>
public sealed class CaptureSessionController : IDisposable
{
    private readonly IHotkeyProvider _hotkeys;
    private readonly IAudioProvider _audio;
    private readonly RecordingCoordinator _coordinator;

    // Hotkey events arrive sequentially, but a press can follow a release
    // faster than StartAsync/StopAsync complete; serialize session work.
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    public event Action<CapturedAudio>? CaptureCompleted;
    public event Action<Exception>? CaptureFailed;

    public CaptureSessionController(
        IHotkeyProvider hotkeys,
        IAudioProvider audio,
        RecordingCoordinator coordinator)
    {
        _hotkeys = hotkeys;
        _audio = audio;
        _coordinator = coordinator;
        _hotkeys.HotkeyChanged += OnHotkeyChanged;
    }

    public RecordingState State => _coordinator.State;

    private async void OnHotkeyChanged(HotkeyEvent hotkeyEvent)
    {
        try
        {
            await ProcessAsync(hotkeyEvent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(exception);
            _coordinator.Reset();
        }
    }

    /// <summary>Exposed for tests; production traffic arrives via the hotkey event.</summary>
    public async Task ProcessAsync(HotkeyEvent hotkeyEvent)
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (hotkeyEvent.Kind == HotkeyEventKind.Pressed)
            {
                await BeginCaptureAsync().ConfigureAwait(false);
            }
            else
            {
                await FinishCaptureAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task BeginCaptureAsync()
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
            CaptureFailed?.Invoke(exception);
        }
    }

    private async Task FinishCaptureAsync()
    {
        if (!_coordinator.TryTransition(RecordingState.Recording, RecordingState.Processing))
        {
            return;
        }

        try
        {
            CapturedAudio captured = await _audio.StopAsync().ConfigureAwait(false);
            CaptureCompleted?.Invoke(captured);
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(exception);
        }
        finally
        {
            _coordinator.TryTransition(RecordingState.Processing, RecordingState.Idle);
        }
    }

    public void Dispose()
    {
        _hotkeys.HotkeyChanged -= OnHotkeyChanged;
        _sessionLock.Dispose();
    }
}
