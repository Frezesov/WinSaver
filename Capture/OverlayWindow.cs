using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinSaver.Core;

namespace WinSaver.Capture;

/// <summary>
/// Covers one monitor with the frozen screenshot of it, pixel for pixel. Each monitor gets its own window so
/// it can render at that monitor's DPI; a single window across monitors with different scaling would blur.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_CLOAK = 13;

    private readonly CaptureSession _session;
    private readonly Image _image;
    private IntPtr _hwnd;

    public OverlayWindow(CaptureSession session, MonitorArea monitor, BitmapSource slice, OverlayToolbar? toolbar)
    {
        _session = session;
        Monitor = monitor;

        Title = "WinSaver";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Bounds.X / monitor.Scale;
        Top = monitor.Bounds.Y / monitor.Scale;
        Width = monitor.Bounds.Width / monitor.Scale;
        Height = monitor.Bounds.Height / monitor.Scale;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        SetResourceReference(FontFamilyProperty, "AppFontFamily");

        _image = new Image
        {
            Source = slice,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        Surface = new OverlaySurface(session, monitor);

        var root = new Grid();
        root.Children.Add(_image);
        root.Children.Add(Surface);
        if (toolbar is not null)
            root.Children.Add(toolbar);
        Content = root;
        ApplyScale(monitor.Scale);
    }

    public MonitorArea Monitor { get; }

    public OverlaySurface Surface { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        SetDwm(Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
        SetDwm(DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        // Hidden from the screen until the first frame is drawn, so no black frame flashes over the desktop.
        SetDwm(DWMWA_CLOAK, 1);
        PlaceOnMonitor();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyScale(newDpi.DpiScaleX);
        PlaceOnMonitor();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => SetDwm(DWMWA_CLOAK, 0));
    }

    // Moving first lets the window take on the target monitor's DPI before it gets its final size in pixels.
    private void PlaceOnMonitor()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        var b = Monitor.Bounds;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, b.X, b.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_NOACTIVATE);
    }

    private void ApplyScale(double scale)
    {
        _image.Width = Monitor.Bounds.Width / scale;
        _image.Height = Monitor.Bounds.Height / scale;
    }

    private void SetDwm(int attribute, int value) =>
        Native.DwmSetWindowAttribute(_hwnd, attribute, ref value, sizeof(int));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _session.PointerDown(this);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _session.CaptureLost(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _session.PointerMove();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _session.PointerUp(this);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _session.Cancel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _session.Cancel();
        }
    }
}
