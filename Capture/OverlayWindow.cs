using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinSaver.Core;
using WinSaver.Editor;

namespace WinSaver.Capture;

/// <summary>
/// Covers one monitor with the frozen screenshot of it, pixel for pixel. Each monitor gets its own window so
/// it can render at that monitor's DPI; a single window across monitors with different scaling would blur.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_CLOAK = 13;
    private const double BarGap = 8;
    private const double BarEdge = 8;

    private readonly CaptureSession _session;
    private readonly Image _image;
    private readonly Grid _root = new();
    private IntPtr _hwnd;

    private EditorCanvas? _board;
    private PixelRect _boardPart;
    private FrameworkElement? _bar;
    private PixelRect _barArea;

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

        _root.Children.Add(_image);
        _root.Children.Add(Surface);
        if (toolbar is not null)
            _root.Children.Add(toolbar);
        Content = _root;
        ApplyScale(monitor.Scale);
    }

    public MonitorArea Monitor { get; }

    public OverlaySurface Surface { get; }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Puts the quick edit canvas over <paramref name="part"/>, the piece of the picked area on this monitor.</summary>
    public void ShowBoard(EditorCanvas board, PixelRect part)
    {
        _board = board;
        _boardPart = part;
        board.HorizontalAlignment = HorizontalAlignment.Left;
        board.VerticalAlignment = VerticalAlignment.Top;
        _root.Children.Insert(_root.Children.IndexOf(Surface) + 1, board);
        PlaceBoard(Scale);
    }

    /// <summary>Shows the quick edit toolbar under the picked area, above it when there is no room, or inside it along the bottom.</summary>
    public void ShowQuickBar(FrameworkElement bar, PixelRect area)
    {
        _bar = bar;
        _barArea = area.Intersect(Monitor.Bounds);
        var layer = new Canvas();
        layer.Children.Add(bar);
        _root.Children.Add(layer);
        PlaceBar(Scale);
    }

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
        if (_board is not null)
            PlaceBoard(scale);
        if (_bar is not null)
            PlaceBar(scale);
    }

    private void PlaceBoard(double scale)
    {
        var local = ToLocal(_boardPart, scale);
        _board!.Margin = new Thickness(local.X, local.Y, 0, 0);
        _board.Width = local.Width;
        _board.Height = local.Height;
    }

    private void PlaceBar(double scale)
    {
        var bar = _bar!;
        double width = Monitor.Bounds.Width / scale, height = Monitor.Bounds.Height / scale;

        // A bar wider than the monitor, e.g. a portrait one at a large scale, is shrunk as a whole.
        bar.LayoutTransform = Transform.Identity;
        bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = bar.DesiredSize;
        double room = width - 2 * BarEdge;
        if (size.Width > room)
        {
            double shrink = room / size.Width;
            bar.LayoutTransform = new ScaleTransform(shrink, shrink);
            size = new Size(size.Width * shrink, size.Height * shrink);
        }

        var area = ToLocal(_barArea, scale);
        double x = Math.Clamp(area.Left + (area.Width - size.Width) / 2, BarEdge, Math.Max(BarEdge, width - size.Width - BarEdge));
        double y;
        if (area.Bottom + BarGap + size.Height <= height - BarEdge)
            y = area.Bottom + BarGap;
        else if (area.Top - BarGap - size.Height >= BarEdge)
            y = area.Top - BarGap - size.Height;
        else
            y = Math.Max(BarEdge, area.Bottom - BarGap - size.Height);
        Canvas.SetLeft(bar, x);
        Canvas.SetTop(bar, y);
    }

    private Rect ToLocal(PixelRect area, double scale) => new(
        (area.X - Monitor.Bounds.X) / scale,
        (area.Y - Monitor.Bounds.Y) / scale,
        area.Width / scale,
        area.Height / scale);

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

    // While drawing, a stray right click must not throw away what is drawn.
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!_session.IsEditing)
            _session.Cancel();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        _session.PreviewKeyDown(e);
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
