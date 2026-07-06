using System.ComponentModel;
using System.Windows;
using WinFlow.Core.Local;
using WinFlow.Core.Local.Models;

namespace WinFlow.App;

/// <summary>
/// Downloads (or removes) the on-device Parakeet model with a live
/// progress bar. The download runs on a background task; progress and
/// completion are marshalled back to the UI thread.
/// </summary>
public partial class LocalModelWindow : Window
{
    private readonly LocalModelManager _manager;
    private readonly LocalModelDescriptor _model = LocalModelCatalog.Default;
    private readonly Action? _onInstalledChanged;
    private CancellationTokenSource? _cts;

    public LocalModelWindow(LocalModelManager manager, Action? onInstalledChanged = null)
    {
        InitializeComponent();
        _manager = manager;
        _onInstalledChanged = onInstalledChanged;
        RefreshState();
    }

    private void RefreshState()
    {
        bool installed = _manager.IsInstalled(_model);
        TitleLabel.Text = _model.DisplayName;
        DetailText(installed);
        Progress.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = installed ? "Remove model" : "Download";
        PrimaryButton.IsEnabled = true;
    }

    private void DetailText(bool installed)
    {
        double mb = _model.TotalBytes / (1024.0 * 1024.0);
        string state = installed
            ? "Installed. WinFlow will use this model in Local or Auto mode — no API key, fully offline."
            : "Not installed yet. This is a free, on-device model (no API key, fully offline).";
        DetailLabel.Text = $"{state}\n\nSize: ~{mb:F0} MB · {_model.ModelType}\n" +
            "Download is resumable — you can close and resume later.";
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
                "Offline model installed. Set STT mode to Local (or Auto) from the tray to use it.",
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
