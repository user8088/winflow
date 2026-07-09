using WinFlow.Core.Models;
using WinFlow.Core.Services;

namespace WinFlow.App;

/// <summary>
/// Bridges pipeline events (raised on capture/network threads) onto the
/// HUD window's dispatcher.
/// </summary>
public sealed class HudController : IDisposable
{
    private readonly HudWindow _hud = new();

    public HudController(DictationPipeline pipeline, RecordingCoordinator coordinator)
    {
        coordinator.StateChanged += state => UiDispatcher.RunOnUiThread(() =>
        {
            switch (state)
            {
                case RecordingState.Recording:
                    _hud.ShowRecording();
                    break;
                case RecordingState.Processing:
                    _hud.ShowProcessing();
                    break;
            }
        });

        pipeline.LevelChanged += rms => _hud.ReportLevel(rms);

        pipeline.DictationCompleted += _ => UiDispatcher.RunOnUiThread(() => _hud.FlashResult("✓", success: true));

        pipeline.DictationFailed += failure => UiDispatcher.RunOnUiThread(() =>
        {
            if (failure.Kind == DictationFailureKind.NoSpeech)
            {
                _hud.HideNow(); // an accidental tap shouldn't flash an error
            }
            else
            {
                _hud.FlashResult(failure.Message, success: false);
            }
        });
    }

    public void Dispose() => UiDispatcher.RunOnUiThread(() => _hud.Close());
}
