using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using WinFlow.Core.Models;
using WinFlow.Core.Services;

namespace WinFlow.App;

/// <summary>
/// Owns the system tray icon: reflects the recording state (gray idle,
/// red recording, amber processing), notifies on saved/failed takes, and
/// hosts the context menu.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly TaskbarIcon _trayIcon;
    private readonly RecordingStore _store;

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
        CaptureSessionController controller,
        RecordingStore store,
        Action onExit)
    {
        _store = store;

        var menu = new ContextMenu();

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
            ToolTipText = "WinFlow — hold Right Ctrl to record",
            ContextMenu = menu,
        };
        _trayIcon.ForceCreate();

        coordinator.StateChanged += state => RunOnUiThread(() => UpdateState(state));
        controller.CaptureCompleted += captured => RunOnUiThread(() => OnCaptureCompleted(captured));
        controller.CaptureFailed += exception => RunOnUiThread(() => OnCaptureFailed(exception));
    }

    /// <summary>
    /// First-launch feedback: the app has no window, so without this a
    /// user who double-clicks the exe sees nothing happen at all.
    /// </summary>
    public void ShowWelcome()
    {
        _trayIcon.ShowNotification(
            "WinFlow is running",
            "Hold Right Ctrl to record, release to save.\nFind the gray dot in the system tray (check the ^ overflow area).",
            NotificationIcon.Info);
    }

    private void UpdateState(RecordingState state)
    {
        (byte[] ico, string toolTip) = state switch
        {
            RecordingState.Recording => (_recordingIco, "WinFlow — recording… release Right Ctrl to stop"),
            RecordingState.Processing => (_processingIco, "WinFlow — saving…"),
            _ => (_idleIco, "WinFlow — hold Right Ctrl to record"),
        };

        System.Drawing.Icon? previous = _currentIcon;
        _currentIcon = TrayIconFactory.CreateIcon(ico);
        _trayIcon.Icon = _currentIcon;
        _trayIcon.ToolTipText = toolTip;
        previous?.Dispose();
    }

    private void OnCaptureCompleted(CapturedAudio captured)
    {
        if (captured.Duration < TimeSpan.FromMilliseconds(150))
        {
            return; // accidental tap, nothing worth keeping
        }

        string path = _store.Save(captured);
        _trayIcon.ShowNotification(
            "Recording saved",
            $"{captured.Duration.TotalSeconds:F1}s · peak RMS {captured.PeakRms:F4}\n{Path.GetFileName(path)}",
            NotificationIcon.Info);
    }

    private void OnCaptureFailed(Exception exception)
    {
        _trayIcon.ShowNotification(
            "Recording failed",
            exception.GetBaseException().Message,
            NotificationIcon.Error);
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
