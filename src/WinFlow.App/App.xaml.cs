using System.Windows;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Audio;
using WinFlow.Core.Hotkeys;
using WinFlow.Core.Injection;
using WinFlow.Core.Mocks;
using WinFlow.Core.Net;
using WinFlow.Core.Security;
using WinFlow.Core.Services;

namespace WinFlow.App;

/// <summary>
/// Tray-resident application: no main window. Hold Right Ctrl to dictate;
/// release to have the transcript typed at the cursor.
///
/// Debug environment variables:
///   WINFLOW_ALLOW_INJECTED=1   — hotkey reacts to SendInput (automated tests)
///   WINFLOW_FAKE_STT=1         — offline fake transcriber instead of OpenAI
///   WINFLOW_SAVE_RECORDINGS=1  — persist every take as WAV (off by default)
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private LowLevelKeyboardHookProvider? _hotkeys;
    private WasapiAudioProvider? _audio;
    private DictationPipeline? _pipeline;
    private OpenAIRealtimeClient? _realtime;
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
        var coordinator = new RecordingCoordinator();
        _hotkeys = new LowLevelKeyboardHookProvider(allowInjected: allowInjected);
        _audio = new WasapiAudioProvider();

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
            _realtime = new OpenAIRealtimeClient(credentials.GetApiKey);
            streaming = _realtime;
            batch = new OpenAIBatchTranscriber(credentials.GetApiKey);
        }

        _pipeline = new DictationPipeline(
            _hotkeys, _audio, coordinator, streaming, batch, new ClipboardTextInjector());

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
        _tray = new TrayIconController(coordinator, _pipeline, credentials, store, ExitApplication);

        bool hasApiKey = fakeStt || !string.IsNullOrEmpty(credentials.GetApiKey());
        if (hasApiKey)
        {
            // Pre-open the first Realtime connection so even the first
            // dictation of the day skips the handshake.
            _realtime?.WarmUpInBackground();
        }

        try
        {
            _hotkeys.Start();
            _tray.ShowWelcome(hasApiKey);
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
        Shutdown();
    }
}
