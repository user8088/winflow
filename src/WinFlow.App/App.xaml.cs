using System.Windows;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Audio;
using WinFlow.Core.Correction;
using WinFlow.Core.Hotkeys;
using WinFlow.Core.Injection;
using WinFlow.Core.Local;
using WinFlow.Core.Local.Models;
using WinFlow.Core.Mocks;
using WinFlow.Core.Models;
using WinFlow.Core.Net;
using WinFlow.Core.Security;
using WinFlow.Core.Services;

namespace WinFlow.App;

/// <summary>
/// Tray-resident application: no main window. Hold Right Ctrl to dictate;
/// release to have the transcript typed at the cursor. Transcription can
/// run in the cloud (OpenAI) or on-device with a free Parakeet model.
///
/// Debug environment variables:
///   WINFLOW_ALLOW_INJECTED=1   — hotkey reacts to SendInput (automated tests)
///   WINFLOW_FAKE_STT=1         — offline fake transcriber instead of any real engine
///   WINFLOW_SAVE_RECORDINGS=1  — persist every take as WAV (off by default)
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private LowLevelKeyboardHookProvider? _hotkeys;
    private WasapiAudioProvider? _audio;
    private DictationPipeline? _pipeline;
    private OpenAIRealtimeClient? _realtime;
    private LocalModelManager? _modelManager;
    private SherpaOnnxSttEngine? _localStt;
    private LlamaCorrectionEngine? _localCorrection;
    private HudController? _hud;
    private TrayIconController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\WinFlow.App", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "WinFlow is already running.\n\nLook for the gray dot in the system tray " +
                "(you may need to click the ^ overflow arrow next to the clock).",
                "WinFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        bool allowInjected = Environment.GetEnvironmentVariable("WINFLOW_ALLOW_INJECTED") == "1";
        bool fakeStt = Environment.GetEnvironmentVariable("WINFLOW_FAKE_STT") == "1";
        bool saveRecordings = Environment.GetEnvironmentVariable("WINFLOW_SAVE_RECORDINGS") == "1";

        var credentials = new WindowsCredentialStore();
        var settingsStore = new SettingsStore();
        AppSettings settings = settingsStore.Current;

        var coordinator = new RecordingCoordinator();
        _hotkeys = new LowLevelKeyboardHookProvider(allowInjected: allowInjected);
        _audio = new WasapiAudioProvider();

        _modelManager = new LocalModelManager(settings.ModelDirectory);

        bool hasApiKey = !string.IsNullOrEmpty(credentials.GetApiKey());

        SttModeController modeController = new(
            settings.SttMode,
            cloudAvailable: () => hasApiKey,
            localAvailable: () => _modelManager.IsInstalled(LocalModelCatalog.Default));
        modeController.BackendChanged += _ =>
            settingsStore.Update(s => s with { SttMode = modeController.ConfiguredMode });

        IStreamingSttProvider streaming;
        IBatchSttProvider batch;
        if (fakeStt)
        {
            var fake = new FakeSttProvider();
            streaming = fake;
            batch = fake;
        }
        else
        {
            _realtime = new OpenAIRealtimeClient(credentials.GetApiKey, nearFieldMic: settings.NearFieldMic);
            var cloudBatch = new OpenAIBatchTranscriber(credentials.GetApiKey);

            // The local engine is cheap to construct (recognizer loads lazily),
            // so we always create it; it's only used when the model is present.
            _localStt = new SherpaOnnxSttEngine(_modelManager);

            var dispatching = new DispatchingSttProvider(modeController, _realtime, cloudBatch, _localStt);
            streaming = dispatching;
            batch = dispatching;
        }

        var inputRouter = new InputMethodRouter(
            new ClipboardTextInjector(),
            new KeystrokeTextInjector(),
            settings.InputMethod);

        var correctionMode = new CorrectionModeController(settings.CorrectionMode);

        TranscriptCorrectionService? correctionService = null;
        if (!fakeStt)
        {
            var cloudCorrector = new OpenAICorrectionClient(credentials.GetApiKey);
            _localCorrection = new LlamaCorrectionEngine(_modelManager);
            var dispatchingCorrector = new DispatchingCorrector(
                modeController, cloudCorrector, _localCorrection);
            correctionService = new TranscriptCorrectionService(
                () => correctionMode.Mode,
                dispatchingCorrector);
        }

        _pipeline = new DictationPipeline(
            _hotkeys, _audio, coordinator, streaming, batch, inputRouter, correctionService);

        var store = new RecordingStore();
        if (saveRecordings)
        {
            _pipeline.AudioCaptured += captured =>
            {
                try
                {
                    store.Save(captured);
                }
                catch
                {
                }
            };
        }

        _hud = new HudController(_pipeline, coordinator);

        _tray = new TrayIconController(
            coordinator, _pipeline, credentials, store,
            modeController,
            correctionMode,
            _modelManager,
            settingsStore,
            inputRouter,
            ExitApplication);

        if (!fakeStt && hasApiKey)
        {
            // Pre-open the first Realtime connection so even the first
            // cloud dictation of the day skips the handshake.
            _realtime?.WarmUpInBackground();
        }

        try
        {
            _hotkeys.Start();
            _tray.ShowWelcome(hasApiKey, _modelManager.IsInstalled(LocalModelCatalog.Default));
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"WinFlow could not install its keyboard hook and will exit.\n\n{exception.Message}",
                "WinFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ExitApplication();
        }
    }

    private void ExitApplication()
    {
        _tray?.Dispose();
        _hud?.Dispose();
        _pipeline?.Dispose();
        _hotkeys?.Dispose();
        _audio?.Dispose();
        _realtime?.Dispose();
        _localStt?.Dispose();
        _localCorrection?.Dispose();
        _modelManager?.Dispose();
        Shutdown();
    }
}
