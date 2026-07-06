using System.Windows;
using WinFlow.Core.Audio;
using WinFlow.Core.Hotkeys;
using WinFlow.Core.Services;

namespace WinFlow.App;

/// <summary>
/// Tray-resident application: no main window. Hold Right Ctrl to record,
/// release to save the take as a WAV (M0 — transcription lands in M1).
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private LowLevelKeyboardHookProvider? _hotkeys;
    private WasapiAudioProvider? _audio;
    private CaptureSessionController? _controller;
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

        // WINFLOW_ALLOW_INJECTED=1 lets automated tests drive the hotkey via
        // SendInput; real installs must ignore synthetic keys so our own
        // future text injection can never retrigger recording.
        bool allowInjected = Environment.GetEnvironmentVariable("WINFLOW_ALLOW_INJECTED") == "1";

        var coordinator = new RecordingCoordinator();
        _hotkeys = new LowLevelKeyboardHookProvider(allowInjected: allowInjected);
        _audio = new WasapiAudioProvider();
        _controller = new CaptureSessionController(_hotkeys, _audio, coordinator);

        var store = new RecordingStore();
        _tray = new TrayIconController(coordinator, _controller, store, ExitApplication);

        try
        {
            _hotkeys.Start();
            _tray.ShowWelcome();
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
        _controller?.Dispose();
        _hotkeys?.Dispose();
        _audio?.Dispose();
        Shutdown();
    }
}
