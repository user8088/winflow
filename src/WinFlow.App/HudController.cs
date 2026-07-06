using System.Windows;
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
        coordinator.StateChanged += state => OnUi(() =>
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

        pipeline.DictationCompleted += _ => OnUi(() => _hud.FlashResult("✓", success: true));

        pipeline.DictationFailed += failure => OnUi(() =>
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

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public void Dispose() => OnUi(() => _hud.Close());
}
