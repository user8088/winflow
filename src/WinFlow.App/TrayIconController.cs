using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using WinFlow.Core.Abstractions;
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
    private System.Drawing.Icon? _currentIcon;

    public TrayIconController(
        RecordingCoordinator coordinator,
        DictationPipeline pipeline,
        ICredentialStore credentials,
        RecordingStore store,
        Action onExit)
    {
        var menu = new ContextMenu();

        var setKey = new MenuItem { Header = "Set OpenAI API key…" };
        setKey.Click += (_, _) => new ApiKeyDialog(credentials).ShowDialog();
        menu.Items.Add(setKey);

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
    public void ShowWelcome(bool hasApiKey)
    {
        _trayIcon.ShowNotification(
            "WinFlow is running",
            hasApiKey
                ? "Hold Right Ctrl, speak, release — text appears at your cursor."
                : "Set your OpenAI API key first: right-click the WinFlow tray dot → Set OpenAI API key.",
            NotificationIcon.Info);
    }

    private void UpdateState(RecordingState state)
    {
        (byte[] ico, string toolTip) = state switch
        {
            RecordingState.Recording => (_recordingIco, "WinFlow — recording… release Right Ctrl to finish"),
            RecordingState.Processing => (_processingIco, "WinFlow — transcribing…"),
            _ => (_idleIco, "WinFlow — hold Right Ctrl to dictate"),
        };

        System.Drawing.Icon? previous = _currentIcon;
        _currentIcon = TrayIconFactory.CreateIcon(ico);
        _trayIcon.Icon = _currentIcon;
        _trayIcon.ToolTipText = toolTip;
        previous?.Dispose();
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
