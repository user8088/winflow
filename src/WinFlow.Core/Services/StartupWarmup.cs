using WinFlow.Core.Audio;
using WinFlow.Core.Correction;
using WinFlow.Core.Local;
using WinFlow.Core.Local.Models;
using WinFlow.Core.Models;
using WinFlow.Core.Net;

namespace WinFlow.Core.Services;

/// <summary>
/// Pre-loads expensive one-time resources (audio device, ONNX recognizer,
/// correction model, cloud WebSocket) so the first dictation feels instant.
/// </summary>
public sealed class StartupWarmup
{
    private readonly Task _warmupTask;
    private volatile bool _isComplete;

    public bool IsComplete => _isComplete;

    public StartupWarmup(
        WasapiAudioProvider? audio,
        OpenAIRealtimeClient? realtime,
        SherpaOnnxSttEngine? localStt,
        LlamaCorrectionEngine? localCorrection,
        LocalModelManager modelManager,
        Func<CorrectionMode> correctionModeProvider,
        SttModeController? modeController = null)
    {
        _warmupTask = RunAsync(
            audio,
            realtime,
            localStt,
            localCorrection,
            modelManager,
            correctionModeProvider,
            modeController);
    }

    public Task WhenReadyAsync(CancellationToken cancellationToken = default) =>
        _warmupTask.WaitAsync(cancellationToken);

    private async Task RunAsync(
        WasapiAudioProvider? audio,
        OpenAIRealtimeClient? realtime,
        SherpaOnnxSttEngine? localStt,
        LlamaCorrectionEngine? localCorrection,
        LocalModelManager modelManager,
        Func<CorrectionMode> correctionModeProvider,
        SttModeController? modeController)
    {
        var work = new List<Task>(4);

        if (audio is not null)
        {
            work.Add(audio.WarmUpDeviceAsync());
        }

        bool warmLocalStt = localStt is not null
            && modelManager.IsInstalled(LocalModelCatalog.Default)
            && (modeController is null || modeController.ResolvedBackend == SttBackend.Local);
        if (warmLocalStt)
        {
            work.Add(localStt!.WarmUpAsync());
        }

        bool warmLocalCorrection = localCorrection is not null
            && modelManager.IsInstalled(LocalModelCatalog.Qwen25Correction)
            && correctionModeProvider() != CorrectionMode.Off;
        if (warmLocalCorrection)
        {
            work.Add(localCorrection!.WarmUpAsync());
        }

        if (realtime is not null)
        {
            realtime.WarmUpInBackground();
        }

        try
        {
            await Task.WhenAll(work).ConfigureAwait(false);
        }
        catch
        {
            // Warm-up is best-effort; dictation still works with on-demand loading.
        }

        if (realtime is not null)
        {
            try
            {
                await realtime.WaitForBackupAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _isComplete = true;
    }
}
