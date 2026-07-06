using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Injection;
using WinFlow.Core.Local;
using WinFlow.Core.Local.Models;
using WinFlow.Core.Models;
using WinFlow.Core.Services;

namespace WinFlow.App;

/// <summary>
/// Owns the system tray icon: reflects the recording state (gray idle,
/// red recording, amber processing), surfaces failures as toasts, and
/// hosts the context menu (API key management, recordings, exit).
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly TaskbarIcon _trayIcon;

    // Icon *bytes* are cached; a fresh Icon is minted per state change and
    // the previous one disposed after handoff. Caching Icon instances is
    // unsafe: swapping them through TaskbarIcon can leave a cached icon
    // disposed, and reusing it later crashes with ObjectDisposedException.
    private readonly byte[] _idleIco = TrayIconFactory.CreateIcoBytes(System.Drawing.Color.Gray);
    private readonly byte[] _recordingIco = TrayIconFactory.CreateIcoBytes(System.Drawing.Color.Crimson);
    private readonly byte[] _processingIco = TrayIconFactory.CreateIcoBytes(System.Drawing.Color.Orange);
    private readonly byte[] _localIco = TrayIconFactory.CreateIcoBytes(System.Drawing.Color.MediumSeaGreen);
    private System.Drawing.Icon? _currentIcon;

    private readonly SttModeController _mode;
    private readonly LocalModelManager _modelManager;
    private readonly SettingsStore _settingsStore;
    private readonly InputMethodRouter _inputRouter;
    private AppSettings _settings;
    private readonly MenuItem _cloudItem;
    private readonly MenuItem _localItem;
    private readonly MenuItem _autoItem;
    private readonly MenuItem _pasteItem;
    private readonly MenuItem _typeItem;
    private readonly MenuItem _autoInputItem;

    public TrayIconController(
        RecordingCoordinator coordinator,
        DictationPipeline pipeline,
        ICredentialStore credentials,
        RecordingStore store,
        SttModeController mode,
        LocalModelManager modelManager,
        SettingsStore settingsStore,
        AppSettings settings,
        InputMethodRouter inputRouter,
        Action onExit)
    {
        _mode = mode;
        _modelManager = modelManager;
        _settingsStore = settingsStore;
        _settings = settings;
        _inputRouter = inputRouter;
        var menu = new ContextMenu();

        var setKey = new MenuItem { Header = "Set OpenAI API key…" };
        setKey.Click += (_, _) => new ApiKeyDialog(credentials).ShowDialog();
        menu.Items.Add(setKey);

        var offline = new MenuItem { Header = "Offline model…" };
        offline.Click += (_, _) =>
        {
            var window = new LocalModelWindow(
                _modelManager, _settingsStore, _settings, RefreshLocalEnabled);
            window.ShowDialog();
            _settings = window.Settings; // pick up any location change
        };
        menu.Items.Add(offline);

        menu.Items.Add(new Separator());

        var modeMenu = new MenuItem { Header = "Transcription mode" };
        _cloudItem = new MenuItem { Header = "Cloud (OpenAI)", IsCheckable = true };
        _cloudItem.Click += (_, _) => SetMode(SttMode.Cloud);
        _localItem = new MenuItem { Header = "Local (offline, free)", IsCheckable = true };
        _localItem.Click += (_, _) => SetMode(SttMode.Local);
        _autoItem = new MenuItem { Header = "Auto", IsCheckable = true };
        _autoItem.Click += (_, _) => SetMode(SttMode.Auto);
        modeMenu.Items.Add(_cloudItem);
        modeMenu.Items.Add(_localItem);
        modeMenu.Items.Add(_autoItem);
        menu.Items.Add(modeMenu);

        var inputMenu = new MenuItem { Header = "Input method" };
        _pasteItem = new MenuItem { Header = "Paste (Ctrl+V)", IsCheckable = true };
        _pasteItem.Click += (_, _) => SetInputMethod(InputMethod.Paste);
        _typeItem = new MenuItem { Header = "Type (best for terminals/Cursor)", IsCheckable = true };
        _typeItem.Click += (_, _) => SetInputMethod(InputMethod.Type);
        _autoInputItem = new MenuItem { Header = "Auto (detect terminals)", IsCheckable = true };
        _autoInputItem.Click += (_, _) => SetInputMethod(InputMethod.Auto);
        inputMenu.Items.Add(_pasteItem);
        inputMenu.Items.Add(_typeItem);
        inputMenu.Items.Add(_autoInputItem);
        menu.Items.Add(inputMenu);

        menu.Items.Add(new Separator());

        var openRecordings = new MenuItem { Header = "Open recordings folder" };
        openRecordings.Click += (_, _) =>
        {
            Directory.CreateDirectory(store.RecordingsDirectory);
            Process.Start(new ProcessStartInfo(store.RecordingsDirectory) { UseShellExecute = true });
        };
        menu.Items.Add(openRecordings);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => onExit();
        menu.Items.Add(exit);

        RefreshModeChecks();
        RefreshLocalEnabled();
        RefreshInputChecks();

        _currentIcon = TrayIconFactory.CreateIcon(_idleIco);
        _trayIcon = new TaskbarIcon
        {
            Icon = _currentIcon,
            ToolTipText = "WinFlow — hold Right Ctrl to dictate",
            ContextMenu = menu,
        };
        _trayIcon.ForceCreate();

        coordinator.StateChanged += state => RunOnUiThread(() => UpdateState(state));
        pipeline.DictationFailed += failure => RunOnUiThread(() => OnDictationFailed(failure));
    }

    /// <summary>
    /// First-launch feedback: the app has no window, so without this a
    /// user who double-clicks the exe sees nothing happen at all.
    /// </summary>
    public void ShowWelcome(bool hasApiKey, bool localAvailable)
    {
        string tip = localAvailable
            ? "Hold Right Ctrl, speak, release — text appears at your cursor. (Using the offline model.)"
            : hasApiKey
                ? "Hold Right Ctrl, speak, release — text appears at your cursor."
                : "Add an OpenAI API key, or download the free offline model: right-click the tray dot.";
        _trayIcon.ShowNotification("WinFlow is running", tip, NotificationIcon.Info);
    }

    private void UpdateState(RecordingState state)
    {
        bool local = _mode.ResolvedBackend == SttBackend.Local;
        byte[] idle = local ? _localIco : _idleIco;

        (byte[] ico, string toolTip) = state switch
        {
            RecordingState.Recording => (_recordingIco, "WinFlow — recording… release Right Ctrl to finish"),
            RecordingState.Processing => (_processingIco, "WinFlow — transcribing…"),
            _ => (idle, local
                ? "WinFlow — Local mode (offline) · hold Right Ctrl to dictate"
                : "WinFlow — hold Right Ctrl to dictate"),
        };

        System.Drawing.Icon? previous = _currentIcon;
        _currentIcon = TrayIconFactory.CreateIcon(ico);
        _trayIcon.Icon = _currentIcon;
        _trayIcon.ToolTipText = toolTip;
        previous?.Dispose();
    }

    private void SetMode(SttMode mode)
    {
        if (mode == SttMode.Local && !_modelManager.IsInstalled(LocalModelCatalog.Default))
        {
            MessageBox.Show(
                "No offline model installed yet. Use 'Offline model…' to download it first.",
                "WinFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            RefreshModeChecks();
            return;
        }

        _mode.Apply(mode);
        RefreshModeChecks();
        UpdateState(RecordingState.Idle);
    }

    private void RefreshModeChecks()
    {
        SttMode configured = _mode.ConfiguredMode;
        _cloudItem.IsChecked = configured == SttMode.Cloud;
        _localItem.IsChecked = configured == SttMode.Local;
        _autoItem.IsChecked = configured == SttMode.Auto;
    }

    private void RefreshLocalEnabled()
    {
        bool installed = _modelManager.IsInstalled(LocalModelCatalog.Default);
        _localItem.IsEnabled = installed;
        _autoItem.Header = installed ? "Auto" : "Auto (downloads offline model to enable Local)";
        _mode.NotifyLocalAvailabilityChanged();
    }

    private void SetInputMethod(InputMethod method)
    {
        _inputRouter.Method = method;
        _settings = _settings with { InputMethod = method };
        _settingsStore.Save(_settings);
        RefreshInputChecks();
    }

    private void RefreshInputChecks()
    {
        InputMethod method = _inputRouter.Method;
        _pasteItem.IsChecked = method == InputMethod.Paste;
        _typeItem.IsChecked = method == InputMethod.Type;
        _autoInputItem.IsChecked = method == InputMethod.Auto;
    }

    private void OnDictationFailed(DictationFailure failure)
    {
        if (failure.Kind == DictationFailureKind.NoSpeech)
        {
            return; // the HUD quietly dismisses; a toast would be noise
        }

        _trayIcon.ShowNotification("WinFlow", failure.Message, NotificationIcon.Warning);
    }

    private static void RunOnUiThread(Action action)
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

    public void Dispose()
    {
        _trayIcon.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
