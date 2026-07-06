using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinFlow.App;

/// <summary>
/// Borderless, non-activating, click-through overlay pinned above the
/// bottom-center of the work area. Shows a live waveform while recording,
/// a processing indicator after key release, and a brief result flash.
/// </summary>
public partial class HudWindow : Window
{
    private const int BarCount = 21;
    private const double MaxBarHeight = 30;
    private const double MinBarHeight = 4;

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _levelHistory = new double[BarCount];
    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _hideTimer;
    private volatile float _latestLevel;

    public HudWindow()
    {
        InitializeComponent();

        var barBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8));
        barBrush.Freeze();
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = 4,
                Height = MinBarHeight,
                RadiusX = 2,
                RadiusY = 2,
                Margin = new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = barBrush,
            };
            BarsPanel.Children.Add(_bars[i]);
        }

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _animationTimer.Tick += (_, _) => AdvanceWaveform();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };

        Loaded += (_, _) => PositionBottomCenter();
    }

    public void ShowRecording()
    {
        _hideTimer.Stop();
        Array.Clear(_levelHistory);
        _latestLevel = 0;
        StatusText.Visibility = Visibility.Collapsed;
        BarsPanel.Visibility = Visibility.Visible;
        PositionBottomCenter();
        Show();
        _animationTimer.Start();
    }

    public void ReportLevel(float rms) => _latestLevel = rms;

    public void ShowProcessing()
    {
        _animationTimer.Stop();
        BarsPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "…";
        StatusText.Visibility = Visibility.Visible;
    }

    public void FlashResult(string message, bool success)
    {
        _animationTimer.Stop();
        BarsPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(
            success ? Color.FromRgb(0x81, 0xC9, 0x95) : Color.FromRgb(0xF2, 0x8B, 0x82));
        StatusText.Visibility = Visibility.Visible;
        if (!IsVisible)
        {
            PositionBottomCenter();
            Show();
        }

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void HideNow()
    {
        _animationTimer.Stop();
        _hideTimer.Stop();
        Hide();
    }

    private void AdvanceWaveform()
    {
        Array.Copy(_levelHistory, 1, _levelHistory, 0, BarCount - 1);
        _levelHistory[BarCount - 1] = Math.Clamp(_latestLevel * 6.0, 0.0, 1.0);

        for (int i = 0; i < BarCount; i++)
        {
            _bars[i].Height = MinBarHeight + _levelHistory[i] * (MaxBarHeight - MinBarHeight);
        }
    }

    private void PositionBottomCenter()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Bottom - Height - 28;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint handle = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(handle, GwlExstyle);
        SetWindowLong(handle, GwlExstyle, exStyle | WsExNoactivate | WsExTransparent | WsExToolwindow);
    }

    private const int GwlExstyle = -20;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int index, int newStyle);
}
