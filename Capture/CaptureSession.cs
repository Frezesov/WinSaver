using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WinSaver.Core;

namespace WinSaver.Capture;

/// <summary>
/// One round of picking an area: freezes the screen, covers every monitor with an overlay, follows the mouse
/// in physical pixels of the virtual desktop (so a selection can cross monitors) and hands back the picked part.
/// </summary>
internal sealed class CaptureSession
{
    private const int MinSelection = 4;

    private readonly List<OverlayWindow> _windows = [];
    private List<MonitorArea> _monitors = [];
    private List<PixelRect> _windowRects = [];
    private BitmapSource? _shot;
    private PixelRect _virtual;
    private Native.POINT _anchor;
    private OverlayToolbar? _toolbar;
    private bool _finished;

    public CaptureSession(CaptureMode mode) => Mode = mode;

    public CaptureMode Mode { get; private set; }

    /// <summary>The area that would be captured now: the rectangle being dragged or the window/screen under the mouse.</summary>
    public PixelRect? Highlight
    {
        get;
        private set
        {
            field = value;
            LabelMonitor = value is { } area ? LabelHost(area) : null;
        }
    }

    /// <summary>The one monitor that shows the size of the highlight, so a selection across monitors gets one label.</summary>
    public MonitorArea? LabelMonitor { get; private set; }

    public bool IsDragging { get; private set; }

    public event Action<CaptureMode>? ModeChanged;

    /// <summary>The captured image, or null when the user cancelled.</summary>
    public event Action<BitmapSource?>? Completed;

    public void Start()
    {
        _virtual = ScreenGrabber.VirtualScreen;
        _shot = ScreenGrabber.Capture(_virtual);
        _windowRects = WindowSnapshot.Collect();
        _monitors = ScreenGrabber.GetMonitors();
        if (_monitors.Count == 0)
            _monitors.Add(new MonitorArea(_virtual, 1));

        Native.GetCursorPos(out var cursor);
        var active = _monitors.FirstOrDefault(m => m.Bounds.Contains(cursor.X, cursor.Y)) ?? _monitors[0];

        foreach (var monitor in _monitors)
        {
            var part = monitor.Bounds.Intersect(_virtual);
            if (part.IsEmpty)
                continue;
            var slice = new CroppedBitmap(_shot, part.Offset(-_virtual.X, -_virtual.Y).ToInt32Rect());
            slice.Freeze();
            OverlayToolbar? toolbar = null;
            if (monitor == active)
                toolbar = _toolbar = new OverlayToolbar(this);
            var window = new OverlayWindow(this, monitor, slice, toolbar);
            _windows.Add(window);
            window.Show();
        }

        var main = _windows.FirstOrDefault(w => w.Monitor == active) ?? _windows.FirstOrDefault();
        if (main is not null)
        {
            Native.ForceForeground(new WindowInteropHelper(main).Handle);
            main.Activate();
            main.Focus();
        }
        UpdateHover();
    }

    public void SetMode(CaptureMode mode)
    {
        if (Mode == mode)
            return;
        Mode = mode;
        ModeChanged?.Invoke(mode);
        Highlight = null;
        UpdateHover();
    }

    public void PointerDown(OverlayWindow window)
    {
        if (_finished)
            return;
        if (Mode == CaptureMode.Rectangle)
        {
            Native.GetCursorPos(out _anchor);
            IsDragging = true;
            Highlight = null;
            window.CaptureMouse();
            _toolbar?.Hide();
            Redraw();
        }
        else if (Highlight is { } target)
        {
            Finish(target);
        }
    }

    public void PointerMove()
    {
        if (_finished)
            return;
        if (IsDragging)
        {
            Native.GetCursorPos(out var p);
            var rect = PixelRect.FromPoints(_anchor.X, _anchor.Y, p.X, p.Y).Intersect(_virtual);
            Highlight = rect.IsEmpty ? null : rect;
            Redraw();
        }
        else
        {
            UpdateHover();
        }
    }

    public void PointerUp(OverlayWindow window)
    {
        if (!IsDragging || _finished)
            return;
        // Releasing the capture raises a mouse move at once, which would already reset the highlight.
        var dragged = Highlight;
        IsDragging = false;
        window.ReleaseMouseCapture();
        if (dragged is { Width: >= MinSelection, Height: >= MinSelection } selection)
        {
            Finish(selection);
            return;
        }
        // A click without a drag picks nothing, like in Snipping Tool.
        Highlight = null;
        _toolbar?.Show();
        Redraw();
    }

    /// <summary>Another window took the mouse mid-drag; the selection ends where the mouse was.</summary>
    public void CaptureLost(OverlayWindow window)
    {
        if (IsDragging)
            PointerUp(window);
    }

    public void Cancel() => Finish(null);

    private void UpdateHover()
    {
        if (IsDragging)
            return;
        PixelRect? target = null;
        if (Mode != CaptureMode.Rectangle)
        {
            Native.GetCursorPos(out var p);
            if (Mode == CaptureMode.Screen)
            {
                target = _monitors.FirstOrDefault(m => m.Bounds.Contains(p.X, p.Y))?.Bounds.Intersect(_virtual);
            }
            else
            {
                foreach (var rect in _windowRects)
                {
                    if (!rect.Contains(p.X, p.Y))
                        continue;
                    var visible = rect.Intersect(_virtual);
                    if (!visible.IsEmpty)
                        target = visible;
                    break;
                }
            }
        }
        if (Highlight == target)
            return;
        Highlight = target;
        Redraw();
    }

    private MonitorArea? LabelHost(PixelRect area) =>
        _monitors.FirstOrDefault(m => m.Bounds.Contains(area.X, area.Bottom - 1))
        ?? _monitors.FirstOrDefault(m => m.Bounds.Contains(area.X, area.Y))
        ?? _monitors.FirstOrDefault(m => !m.Bounds.Intersect(area).IsEmpty);

    private void Redraw()
    {
        foreach (var window in _windows)
            window.Surface.InvalidateVisual();
    }

    private void Finish(PixelRect? area)
    {
        if (_finished)
            return;
        _finished = true;

        BitmapSource? image = null;
        if (area is { IsEmpty: false } a && _shot is not null)
            image = ScreenGrabber.CopyRegion(_shot, a.Offset(-_virtual.X, -_virtual.Y));

        foreach (var window in _windows)
            window.Close();
        _windows.Clear();
        _shot = null;
        Completed?.Invoke(image);
    }
}
