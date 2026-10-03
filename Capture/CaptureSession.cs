using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WinSaver.Core;
using WinSaver.ViewModels;
using CaptureMode = WinSaver.Core.CaptureMode;

namespace WinSaver.Capture;

/// <summary>
/// One round of picking an area: freezes the screen, covers every monitor with an overlay, follows the mouse
/// in physical pixels of the virtual desktop (so a selection can cross monitors) and hands back the picked part.
/// With the quick edit on, the overlay stays up after picking so the part can be drawn on first.
/// </summary>
internal sealed class CaptureSession
{
    private const int MinSelection = 4;

    private readonly MainViewModel? _quickEdit;
    private readonly List<OverlayWindow> _windows = [];
    private List<MonitorArea> _monitors = [];
    private List<PixelRect> _windowRects = [];
    private BitmapSource? _shot;
    private PixelRect _virtual;
    private Native.POINT _anchor;
    private OverlayToolbar? _toolbar;
    private QuickEditor? _editor;
    private bool _finished;

    /// <param name="quickEdit">Where the quick edit takes its tool, colour and thickness from; null when it is off.</param>
    public CaptureSession(CaptureMode mode, MainViewModel? quickEdit)
    {
        Mode = mode;
        _quickEdit = quickEdit;
    }

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

    /// <summary>The area is picked and is being drawn on.</summary>
    public bool IsEditing => _editor is not null;

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
        if (_finished || IsEditing)
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
            Pick(target);
        }
    }

    public void PointerMove()
    {
        if (_finished || IsEditing)
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
            Pick(selection);
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

    public void PreviewKeyDown(KeyEventArgs e) => _editor?.PreviewKeyDown(e);

    public void Cancel() => Close(null);

    /// <summary>
    /// An overlay is closing. Unless the session closes it itself, e.g. after Alt + F4, the whole round ends
    /// without a picture: otherwise the other monitors stay covered and the next capture never starts.
    /// </summary>
    public void WindowClosing(OverlayWindow window)
    {
        if (_finished)
            return;
        _windows.Remove(window);
        Cancel();
    }

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

    private void Pick(PixelRect area)
    {
        if (_finished)
            return;
        if (area.IsEmpty || _shot is null)
        {
            Close(null);
            return;
        }
        var image = ScreenGrabber.CopyRegion(_shot, area.Offset(-_virtual.X, -_virtual.Y));
        if (_quickEdit is null)
            Close(image);
        else
            BeginEdit(_quickEdit, area, image);
    }

    private void BeginEdit(MainViewModel vm, PixelRect area, BitmapSource image)
    {
        Highlight = area;
        _toolbar?.Hide();
        foreach (var window in _windows)
            window.Cursor = Cursors.Arrow;
        var host = _windows.FirstOrDefault(w => w.Monitor == LabelMonitor) ?? _windows[0];
        _editor = new QuickEditor(vm, image, area, _windows, host);
        _editor.Completed += Close;
        Redraw();
    }

    private void Close(BitmapSource? image)
    {
        if (_finished)
            return;
        _finished = true;
        foreach (var window in _windows)
            window.Close();
        _windows.Clear();
        _shot = null;
        _editor = null;
        Completed?.Invoke(image);
    }
}
