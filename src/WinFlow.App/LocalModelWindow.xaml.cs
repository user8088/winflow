using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WinFlow.Core.Local;
using WinFlow.Core.Local.Models;
using WinFlow.Core.Services;

namespace WinFlow.App;

/// <summary>
/// Downloads (or removes) the on-device Parakeet model with a live progress
/// bar. The user chooses the install location via a folder picker; the choice
/// is persisted in settings so it sticks across launches without an env var.
/// </summary>
public partial class LocalModelWindow : Window
{
    private readonly LocalModelManager _manager;
    private readonly SettingsStore _settingsStore;
    private readonly LocalModelDescriptor _model;
    private readonly Action? _onInstalledChanged;
    private CancellationTokenSource? _cts;

    public LocalModelWindow(
        LocalModelManager manager,
        SettingsStore settingsStore,
        LocalModelDescriptor? model = null,
        Action? onInstalledChanged = null)
    {
        InitializeComponent();
        _manager = manager;
        _settingsStore = settingsStore;
        _model = model ?? LocalModelCatalog.Default;
        _onInstalledChanged = onInstalledChanged;
        string? modelDirectory = _settingsStore.Current.ModelDirectory;
        if (!string.IsNullOrWhiteSpace(modelDirectory))
        {
            _manager.UseRoot(modelDirectory);
        }
        RefreshState();
    }

    private void RefreshState()
    {
        bool installed = _manager.IsInstalled(_model);
        TitleLabel.Text = _model.DisplayName;
        PathText.Text = _manager.ModelDirectory(_model);
        DriveText.Text = DriveSummary();
        DetailLabel.Text = installed
            ? InstalledMessage(_model)
            : NotInstalledMessage(_model);
        Progress.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = installed ? "Remove model" : "Download";
        PrimaryButton.IsEnabled = true;
    }

    private static string InstalledMessage(LocalModelDescriptor model) =>
        model.Id == LocalModelCatalog.Qwen25CorrectionGguf
            ? "Installed. WinFlow will use this model when correcting transcripts in Local mode."
            : "Installed. WinFlow will use this model in Local or Auto mode — no API key, fully offline.";

    private static string NotInstalledMessage(LocalModelDescriptor model) =>
        model.Id == LocalModelCatalog.Qwen25CorrectionGguf
            ? "Not installed yet. A free on-device model for fixing grammar and broken English offline.\nDownload is resumable — you can close and resume later."
            : "Not installed yet. A free, on-device model (no API key, fully offline).\nDownload is resumable — you can close and resume later.";

    private string DriveSummary()
    {
        double needMb = _model.TotalBytes / (1024.0 * 1024.0);
        long? free = _manager.GetAvailableBytes();
        if (free is long f)
        {
            double freeMb = f / (1024.0 * 1024.0);
            string note = f < _model.TotalBytes ? "  — NOT ENOUGH SPACE on this drive" : "";
            return $"Free here: {freeMb:F0} MB · needs ~{needMb:F0} MB{note}";
        }

        return $"Needs ~{needMb:F0} MB";
    }

    private void OnChangeLocation(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose where to store the WinFlow offline model",
            InitialDirectory = _manager.ModelDirectory(_model),
        };

        if (picker.ShowDialog() != true)
        {
            return;
        }

        string chosen = picker.FolderName;
        _manager.UseRoot(chosen);
        _settingsStore.Update(s => s with { ModelDirectory = chosen });
        RefreshState();
    }

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_manager.IsInstalled(_model))
        {
            PrimaryButton.IsEnabled = false;
            try
            {
                _manager.Delete(_model);
                _onInstalledChanged?.Invoke();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.GetBaseException().Message, "WinFlow");
            }

            RefreshState();
            return;
        }

        if (_manager.GetAvailableBytes() is long free && free < _model.TotalBytes)
        {
            MessageBox.Show(
                this,
                "Not enough free space on the chosen drive. Pick another location with Change…",
                "WinFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = "Cancel";
        Progress.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "Starting…";

        _cts = new CancellationTokenSource();
        try
        {
            _manager.ProgressChanged += OnProgress;
            _manager.FileCompleted += OnFileCompleted;
            await _manager.EnsureInstalledAsync(_model, _cts.Token);
            _onInstalledChanged?.Invoke();
            RefreshState();
            MessageBox.Show(
                this,
                _model.Id == LocalModelCatalog.Qwen25CorrectionGguf
                    ? "Correction model installed. It will be used when transcribing in Local mode with correction enabled."
                    : "Offline model installed. Set STT mode to Local (or Auto) from the tray to use it.",
                "WinFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled. Partial download will resume next time.";
            PrimaryButton.Content = "Download";
            PrimaryButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.GetBaseException().Message, "Download failed");
            RefreshState();
        }
        finally
        {
            _manager.ProgressChanged -= OnProgress;
            _manager.FileCompleted -= OnFileCompleted;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnProgress(double fraction, string detail)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Progress.Value = fraction * 100;
            StatusText.Text = detail;
        });
    }

    private void OnFileCompleted(string file)
    {
        Dispatcher.BeginInvoke(() => StatusText.Text = $"Verified {file}");
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
