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
        Rect workArea = GetActiveWorkArea();
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Bottom - Height - 28;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED));
    }

    /// <summary>
    /// Work area (in WPF device-independent units) of the monitor hosting the
    /// foreground window — i.e. the app being dictated into — falling back to
    /// the cursor's monitor, then the primary work area.
    /// </summary>
    private Rect GetActiveWorkArea()
    {
        nint monitor = 0;
        nint foreground = GetForegroundWindow();
        if (foreground != 0)
        {
            monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        }

        if (monitor == 0 && GetCursorPos(out NativePoint cursor))
        {
            monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        }

        if (monitor != 0)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                return PixelsToDips(info.WorkArea);
            }
        }

        return SystemParameters.WorkArea;
    }

    /// <summary>
    /// Converts a native pixel rect into DIPs. The app has no manifest, so the
    /// process is system-DPI-aware: Win32 coordinates are already virtualized
    /// to the single system DPI, and one uniform scale applies everywhere.
    /// </summary>
    private Rect PixelsToDips(NativeRect rect)
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            Matrix fromDevice = target.TransformFromDevice;
            Point topLeft = fromDevice.Transform(new Point(rect.Left, rect.Top));
            Point bottomRight = fromDevice.Transform(new Point(rect.Right, rect.Bottom));
            return new Rect(topLeft, bottomRight);
        }

        // No HWND yet (called before the window is first shown); GetDpi still
        // reports the system DPI scale, which is the correct divisor here.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            rect.Left / dpi.DpiScaleX,
            rect.Top / dpi.DpiScaleY,
            (rect.Right - rect.Left) / dpi.DpiScaleX,
            (rect.Bottom - rect.Top) / dpi.DpiScaleY);
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
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint pt);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
