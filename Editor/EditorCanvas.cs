using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinSaver.Editor;

/// <summary>
/// Shows the screenshot fitted into the window and turns mouse gestures into annotations. Everything is kept in
/// image pixels; one transform maps them to the screen, so zooming and panning never redraw the picture.
/// The finished drawing lives in one layer and the stroke in progress in another, so only that one is redrawn
/// while the mouse moves.
/// </summary>
internal sealed class EditorCanvas : FrameworkElement
{
    private const double Padding = 24;
    private const double MinZoom = 0.05;
    private const double MaxZoom = 8;
    private const double HandleSize = 10;

    private static readonly Brush CropDim = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Brush PreviewFill = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
    // Mid grey reads as an edge on both light and dark backgrounds, so a dark screenshot does not melt into the window.
    private static readonly Brush FrameBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)));

    private readonly VisualCollection _children;
    private readonly DrawingVisual _content = new();
    private readonly DrawingVisual _live = new();
    private readonly MatrixTransform _view = new();
    private EditDocument? _doc;
    private Int32Rect _shownCrop;

    private double _zoom = 1;
    private Vector _offset;
    private bool _fit = true;
    private Point? _pinned;

    private Gesture _gesture;
    private readonly List<Point> _points = [];
    private Point _anchor;
    private Point _current;
    private Point _panStart;
    private Vector _panOffset;
    private bool _spaceDown;

    private bool _cropping;
    private Int32Rect _cropRect;
    private Int32Rect _cropStart;
    private CropHandle _cropHandle;

    private TextBox? _textBox;
    private Point _textOrigin;
    private TextAnnotation? _editingText;

    private EditorTool _tool;
    private EditorTool _toolBeforeCrop = EditorTool.Pen;
    private Color _color = Colors.Red;
    private double _size = 4;

    private enum Gesture { None, Ink, Shape, Erase, Pan, Crop }

    private enum CropHandle { None, New, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    public EditorCanvas()
    {
        _children = new VisualCollection(this) { _content, _live };
        _content.Transform = _view;
        _live.Transform = _view;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        UpdateCursor();
    }

    public event Action? ViewChanged;
    public event Action? CropModeChanged;
    public event Action? ToolChanged;

    public double ZoomPercent => _zoom * 100;

    public bool IsCropping => _cropping;

    public bool IsBusy => _gesture != Gesture.None || _textBox is not null;

    public FontFamily TextFont { get; set; } = new("Segoe UI");

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
                return;
            CommitText();
            CancelGesture();
            if (_cropping)
                EndCrop(apply: true);
            if (value == EditorTool.Crop)
                _toolBeforeCrop = _tool;
            _tool = value;
            if (value == EditorTool.Crop)
                BeginCrop();
            UpdateCursor();
            ToolChanged?.Invoke();
        }
    }

    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            if (_textBox is not null)
                _textBox.Foreground = _textBox.CaretBrush = new SolidColorBrush(value);
        }
    }

    /// <summary>Line thickness as it looks on screen, in DIPs; stored strokes get it in image pixels.</summary>
    public double Size
    {
        get => _size;
        set
        {
            _size = value;
            if (_textBox is not null && _editingText is null)
                _textBox.FontSize = TextSizeDip;
        }
    }

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private double Scale => _zoom / Dpi;

    private double TextSizeDip => 14 + _size * 3;

    private Rect ViewRect
    {
        get
        {
            if (_doc is null)
                return new Rect(0, 0, 1, 1);
            var r = _cropping ? _doc.FullRect : _doc.Crop;
            return new Rect(r.X, r.Y, r.Width, r.Height);
        }
    }

    public void Load(EditDocument doc)
    {
        if (_doc is not null)
        {
            _doc.Changed -= OnDocumentChanged;
            _doc.DraftChanged -= RenderLive;
        }
        _doc = doc;
        _doc.Changed += OnDocumentChanged;
        _doc.DraftChanged += RenderLive;
        _shownCrop = doc.Crop;
        RenderContent();
        Fit();
    }

    private void OnDocumentChanged()
    {
        if (_doc is not null && !_doc.Crop.Equals(_shownCrop))
        {
            _shownCrop = _doc.Crop;
            Fit();
        }
        RenderContent();
    }

    // ---- View

    /// <summary>
    /// Shows the picture one image pixel per device pixel with <paramref name="origin"/> of it at the top-left
    /// corner, for drawing right on the frozen screen: no zoom, scrolling, panning or frame.
    /// </summary>
    public void PinView(Point origin)
    {
        _pinned = origin;
        Fit();
    }

    public void Fit()
    {
        _fit = true;
        if (_pinned is { } origin)
        {
            _zoom = 1;
            _offset = new Vector(-origin.X / Dpi, -origin.Y / Dpi);
            UpdateTransform();
            return;
        }
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var view = ViewRect;
        double width = Math.Max(1, ActualWidth - 2 * Padding), height = Math.Max(1, ActualHeight - 2 * Padding);
        _zoom = Math.Clamp(Math.Min(1, Math.Min(width * Dpi / view.Width, height * Dpi / view.Height)), MinZoom, MaxZoom);
        Center();
    }

    public void ZoomBy(double factor) =>
        ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), _zoom * factor);

    private void Center()
    {
        var view = ViewRect;
        double dpi = Dpi;
        // Whole device pixels keep a 100 % view pixel-exact.
        _offset = new Vector(
            Math.Round((ActualWidth - view.Width * Scale) / 2 * dpi) / dpi,
            Math.Round((ActualHeight - view.Height * Scale) / 2 * dpi) / dpi);
        UpdateTransform();
    }

    private void ZoomAt(Point screen, double zoom)
    {
        CommitText();
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var anchor = ToImage(screen);
        _zoom = zoom;
        _fit = false;
        var view = ViewRect;
        _offset = new Vector(screen.X - (anchor.X - view.X) * Scale, screen.Y - (anchor.Y - view.Y) * Scale);
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        var view = ViewRect;
        double s = Scale;
        _view.Matrix = new Matrix(s, 0, 0, s, _offset.X - view.X * s, _offset.Y - view.Y * s);
        RenderOptions.SetBitmapScalingMode(_content, _zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        RenderLive();
        InvalidateArrange();
        ViewChanged?.Invoke();
    }

    private Point ToImage(Point screen)
    {
        var view = ViewRect;
        return new Point((screen.X - _offset.X) / Scale + view.X, (screen.Y - _offset.Y) / Scale + view.Y);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fit)
        {
            Fit();
        }
        else
        {
            _offset += new Vector(sizeInfo.NewSize.Width - sizeInfo.PreviousSize.Width, sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height) / 2;
            UpdateTransform();
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_fit)
            Fit();
        else
            UpdateTransform();
    }

    // ---- Drawing

    private void RenderContent()
    {
        if (_doc is null)
            return;
        using (var dc = _content.RenderOpen())
            _doc.Render(dc, _editingText);
        var crop = _doc.Crop;
        _content.Clip = _cropping ? null : new RectangleGeometry(new Rect(crop.X, crop.Y, crop.Width, crop.Height));
    }

    private void RenderLive()
    {
        using var dc = _live.RenderOpen();
        if (_doc is null)
            return;
        if (_cropping)
            DrawCrop(dc);
        else if (_pinned is null)
            DrawFrame(dc);
        if (_doc.Draft is { } draft)
            dc.DrawDrawing(draft);
    }

    private void UpdateDraft()
    {
        if (_doc is null)
            return;
        _doc.Draft = _gesture switch
        {
            Gesture.Ink => Record(dc => CreateInk()?.Render(dc)),
            Gesture.Shape => Record(DrawShapePreview),
            _ => null,
        };
    }

    private static Drawing Record(Action<DrawingContext> draw)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
            draw(dc);
        drawing.Freeze();
        return drawing;
    }

    private void DrawFrame(DrawingContext dc)
    {
        var view = ViewRect;
        double px = 1 / Scale;
        view.Inflate(px / 2, px / 2);
        dc.DrawRectangle(null, new Pen(FrameBrush, px), view);
    }

    private void DrawShapePreview(DrawingContext dc)
    {
        var rect = ShapeRect();
        if (_tool == EditorTool.Pixelate)
        {
            var pen = new Pen(Brushes.White, 1.5 / Scale) { DashStyle = new DashStyle([4, 3], 0) };
            dc.DrawRectangle(PreviewFill, pen, rect);
        }
        else if (rect.Width > 0 && rect.Height > 0)
        {
            new ShapeAnnotation(rect, _color, StrokeWidth, _tool == EditorTool.Ellipse).Render(dc);
        }
    }

    private void DrawCrop(DrawingContext dc)
    {
        var full = new Rect(0, 0, _doc!.Image.PixelWidth, _doc.Image.PixelHeight);
        var crop = new Rect(_cropRect.X, _cropRect.Y, _cropRect.Width, _cropRect.Height);
        double px = 1 / Scale;

        dc.DrawGeometry(CropDim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(crop)));

        var thirds = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), px);
        for (int i = 1; i <= 2; i++)
        {
            double x = crop.X + crop.Width * i / 3, y = crop.Y + crop.Height * i / 3;
            dc.DrawLine(thirds, new Point(x, crop.Top), new Point(x, crop.Bottom));
            dc.DrawLine(thirds, new Point(crop.Left, y), new Point(crop.Right, y));
        }
        dc.DrawRectangle(null, new Pen(Brushes.White, 1.5 * px), crop);

        var handleFill = Brushes.White;
        var handlePen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), px);
        double h = HandleSize * px;
        foreach (var point in HandlePoints(crop))
            dc.DrawRectangle(handleFill, handlePen, new Rect(point.X - h / 2, point.Y - h / 2, h, h));
    }

    private static IEnumerable<Point> HandlePoints(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return r.TopLeft;
        yield return new Point(cx, r.Top);
        yield return r.TopRight;
        yield return new Point(r.Right, cy);
        yield return r.BottomRight;
        yield return new Point(cx, r.Bottom);
        yield return r.BottomLeft;
        yield return new Point(r.Left, cy);
    }

    protected override void OnRender(DrawingContext dc) =>
        // Transparent, but it makes the whole area take mouse input.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

    // ---- Strokes

    private double StrokeWidth => _size / Scale;

    private InkAnnotation? CreateInk()
    {
        if (_points.Count == 0)
            return null;
        return _tool switch
        {
            EditorTool.Marker => new InkAnnotation(_points, _color, (_size * 3 + 6) / Scale, InkKind.Marker),
            EditorTool.Arrow => _points.Count < 2 ? null : new InkAnnotation(_points, _color, StrokeWidth, InkKind.Arrow),
            _ => new InkAnnotation(_points, _color, StrokeWidth, InkKind.Pen),
        };
    }

    private Rect ShapeRect()
    {
        var a = _anchor;
        var b = _current;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _tool is EditorTool.Rectangle or EditorTool.Ellipse)
        {
            double side = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            b = new Point(a.X + Math.Sign(b.X - a.X) * side, a.Y + Math.Sign(b.Y - a.Y) * side);
        }
        return new Rect(a, b);
    }

    private Int32Rect PixelRect(Rect rect)
    {
        var image = _doc!.FullRect;
        int left = Math.Clamp((int)Math.Floor(rect.Left), 0, image.Width);
        int top = Math.Clamp((int)Math.Floor(rect.Top), 0, image.Height);
        int right = Math.Clamp((int)Math.Ceiling(rect.Right), 0, image.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, image.Height);
        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // ---- Mouse

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_doc is null)
            return;
        Focus();
        var screen = e.GetPosition(this);
        var point = ToImage(screen);

        if (_pinned is null && (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _spaceDown)))
        {
            CommitText();
            _gesture = Gesture.Pan;
            _panStart = screen;
            _panOffset = _offset;
            Cursor = Cursors.SizeAll;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (_textBox is not null)
        {
            CommitText();
            e.Handled = true;
            return;
        }

        e.Handled = true;
        switch (_tool)
        {
            case EditorTool.Pen or EditorTool.Marker or EditorTool.Arrow:
                _gesture = Gesture.Ink;
                _points.Clear();
                _points.Add(point);
                _anchor = point;
                break;
            case EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Pixelate:
                _gesture = Gesture.Shape;
                _anchor = _current = point;
                break;
            case EditorTool.Eraser:
                _gesture = Gesture.Erase;
                EraseAt(point);
                break;
            case EditorTool.Text:
                BeginText(point);
                return;
            case EditorTool.Crop:
                BeginCropDrag(screen, point);
                break;
        }
        CaptureMouse();
        UpdateDraft();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_doc is null)
            return;
        var screen = e.GetPosition(this);
        var point = ToImage(screen);

        switch (_gesture)
        {
            case Gesture.Pan:
                _offset = _panOffset + (screen - _panStart);
                _fit = false;
                UpdateTransform();
                return;
            case Gesture.Ink:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    _points.Clear();
                    _points.Add(_anchor);
                    _points.Add(point);
                }
                else if ((point - _points[^1]).Length * Scale >= 1.5)
                {
                    _points.Add(point);
                }
                UpdateDraft();
                return;
            case Gesture.Shape:
                _current = point;
                UpdateDraft();
                return;
            case Gesture.Erase:
                EraseAt(point);
                return;
            case Gesture.Crop:
                DragCrop(point);
                return;
        }

        if (_cropping)
            Cursor = CursorFor(HitCropHandle(screen));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_doc is null || _gesture == Gesture.None)
            return;
        var gesture = _gesture;
        _gesture = Gesture.None;
        ReleaseMouseCapture();

        switch (gesture)
        {
            case Gesture.Pan:
                UpdateCursor();
                break;
            case Gesture.Ink:
                if (CreateInk() is { } ink)
                    _doc.Add(ink);
                _points.Clear();
                break;
            case Gesture.Shape:
                CommitShape();
                break;
        }
        _doc.Draft = null;
        RenderLive();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_doc is null || _pinned is not null)
            return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomAt(e.GetPosition(this), _zoom * Math.Pow(1.15, e.Delta / 120.0));
            return;
        }
        CommitText();
        var delta = e.Delta / 120.0 * 48;
        _offset += Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? new Vector(delta, 0) : new Vector(0, delta);
        _fit = false;
        UpdateTransform();
    }

    public void SetSpaceDown(bool down)
    {
        if (_spaceDown == down)
            return;
        _spaceDown = down;
        if (_gesture == Gesture.None)
            UpdateCursor();
    }

    private void CommitShape()
    {
        var rect = ShapeRect();
        double min = 2 / Scale;
        if (rect.Width < min && rect.Height < min)
            return;
        if (_tool == EditorTool.Pixelate)
        {
            var area = PixelRect(rect);
            if (area.Width >= 4 && area.Height >= 4)
                _doc!.Add(new PixelateAnnotation(_doc.Image, area));
        }
        else
        {
            _doc!.Add(new ShapeAnnotation(rect, _color, StrokeWidth, _tool == EditorTool.Ellipse));
        }
    }

    private void EraseAt(Point point)
    {
        if (_doc?.HitTest(point, 6 / Scale) is { } hit)
            _doc.Remove(hit);
    }

    /// <summary>Drops the gesture in progress; returns false when there was nothing to drop.</summary>
    public bool CancelGesture()
    {
        if (_textBox is not null)
        {
            CloseTextBox();
            return true;
        }
        if (_gesture == Gesture.None)
            return false;
        _gesture = Gesture.None;
        _points.Clear();
        ReleaseMouseCapture();
        UpdateCursor();
        _doc?.Draft = null;
        RenderLive();
        return true;
    }

    private void UpdateCursor() => Cursor = _spaceDown ? Cursors.SizeAll : _tool switch
    {
        EditorTool.Text => Cursors.IBeam,
        EditorTool.Eraser => Cursors.Hand,
        EditorTool.Crop => Cursors.Arrow,
        _ => Cursors.Cross,
    };

    // ---- Crop

    private void BeginCrop()
    {
        if (_doc is null || _cropping)
            return;
        _cropping = true;
        _cropRect = _doc.Crop;
        RenderContent();
        Fit();
        CropModeChanged?.Invoke();
    }

    /// <summary>Keeps the frame and returns to the tool used before cropping.</summary>
    public void ApplyCrop() => LeaveCrop(apply: true);

    public void CancelCrop() => LeaveCrop(apply: false);

    private void LeaveCrop(bool apply)
    {
        if (!_cropping)
            return;
        EndCrop(apply);
        _tool = _toolBeforeCrop;
        UpdateCursor();
        ToolChanged?.Invoke();
    }

    private void EndCrop(bool apply)
    {
        _cropping = false;
        if (apply && _doc is not null)
            _doc.SetCrop(_cropRect.Width >= 1 && _cropRect.Height >= 1 ? _cropRect : _doc.FullRect);
        FinishCrop();
    }

    public void ResetCrop()
    {
        if (!_cropping || _doc is null)
            return;
        _cropRect = _doc.FullRect;
        RenderLive();
    }

    private void FinishCrop()
    {
        _gesture = Gesture.None;
        RenderContent();
        Fit();
        UpdateCursor();
        CropModeChanged?.Invoke();
    }

    private void BeginCropDrag(Point screen, Point point)
    {
        _cropHandle = HitCropHandle(screen);
        _cropStart = _cropRect;
        _anchor = point;
        _gesture = Gesture.Crop;
    }

    private void DragCrop(Point point)
    {
        var image = _doc!.FullRect;
        int dx = (int)Math.Round(point.X - _anchor.X), dy = (int)Math.Round(point.Y - _anchor.Y);
        int left = _cropStart.X, top = _cropStart.Y, right = _cropStart.X + _cropStart.Width, bottom = _cropStart.Y + _cropStart.Height;
        const int min = 8;

        switch (_cropHandle)
        {
            case CropHandle.Move:
                dx = Math.Clamp(dx, -left, image.Width - right);
                dy = Math.Clamp(dy, -top, image.Height - bottom);
                left += dx; right += dx; top += dy; bottom += dy;
                break;
            case CropHandle.New:
                left = (int)Math.Round(_anchor.X);
                top = (int)Math.Round(_anchor.Y);
                right = (int)Math.Round(point.X);
                bottom = (int)Math.Round(point.Y);
                if (right < left) (left, right) = (right, left);
                if (bottom < top) (top, bottom) = (bottom, top);
                break;
            default:
                if (_cropHandle is CropHandle.Left or CropHandle.TopLeft or CropHandle.BottomLeft)
                    left = Math.Min(left + dx, right - min);
                if (_cropHandle is CropHandle.Right or CropHandle.TopRight or CropHandle.BottomRight)
                    right = Math.Max(right + dx, left + min);
                if (_cropHandle is CropHandle.Top or CropHandle.TopLeft or CropHandle.TopRight)
                    top = Math.Min(top + dy, bottom - min);
                if (_cropHandle is CropHandle.Bottom or CropHandle.BottomLeft or CropHandle.BottomRight)
                    bottom = Math.Max(bottom + dy, top + min);
                break;
        }

        left = Math.Clamp(left, 0, image.Width);
        right = Math.Clamp(right, 0, image.Width);
        top = Math.Clamp(top, 0, image.Height);
        bottom = Math.Clamp(bottom, 0, image.Height);
        if (right - left >= 1 && bottom - top >= 1)
            _cropRect = new Int32Rect(left, top, right - left, bottom - top);
        RenderLive();
    }

    private CropHandle HitCropHandle(Point screen)
    {
        var tl = _view.Transform(new Point(_cropRect.X, _cropRect.Y));
        var br = _view.Transform(new Point(_cropRect.X + _cropRect.Width, _cropRect.Y + _cropRect.Height));
        const double reach = 12;
        bool nearLeft = Math.Abs(screen.X - tl.X) <= reach, nearRight = Math.Abs(screen.X - br.X) <= reach;
        bool nearTop = Math.Abs(screen.Y - tl.Y) <= reach, nearBottom = Math.Abs(screen.Y - br.Y) <= reach;
        bool withinX = screen.X > tl.X - reach && screen.X < br.X + reach;
        bool withinY = screen.Y > tl.Y - reach && screen.Y < br.Y + reach;

        if (nearLeft && nearTop) return CropHandle.TopLeft;
        if (nearRight && nearTop) return CropHandle.TopRight;
        if (nearLeft && nearBottom) return CropHandle.BottomLeft;
        if (nearRight && nearBottom) return CropHandle.BottomRight;
        if (nearLeft && withinY) return CropHandle.Left;
        if (nearRight && withinY) return CropHandle.Right;
        if (nearTop && withinX) return CropHandle.Top;
        if (nearBottom && withinX) return CropHandle.Bottom;
        if (screen.X > tl.X && screen.X < br.X && screen.Y > tl.Y && screen.Y < br.Y) return CropHandle.Move;
        return CropHandle.New;
    }

    private static Cursor CursorFor(CropHandle handle) => handle switch
    {
        CropHandle.TopLeft or CropHandle.BottomRight => Cursors.SizeNWSE,
        CropHandle.TopRight or CropHandle.BottomLeft => Cursors.SizeNESW,
        CropHandle.Left or CropHandle.Right => Cursors.SizeWE,
        CropHandle.Top or CropHandle.Bottom => Cursors.SizeNS,
        CropHandle.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    // ---- Text

    private void BeginText(Point point)
    {
        var existing = _doc!.HitTest(point, 4 / Scale) as TextAnnotation;
        _editingText = existing;
        _textOrigin = existing?.Origin ?? point;
        var color = existing?.Color ?? _color;

        _textBox = new TextBox
        {
            Text = existing?.Text ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = TextFont,
            FontWeight = FontWeights.SemiBold,
            FontSize = existing is null ? TextSizeDip : existing.FontSize * Scale,
            Foreground = new SolidColorBrush(color),
            CaretBrush = new SolidColorBrush(color),
            MinWidth = 24,
        };
        _textBox.SetResourceReference(StyleProperty, "BareTextBox");
        _textBox.PreviewKeyDown += OnTextKeyDown;
        _textBox.LostKeyboardFocus += (_, _) => CommitText();
        _children.Add(_textBox);
        if (existing is not null)
            RenderContent();
        InvalidateMeasure();
        InvalidateArrange();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            _textBox?.Focus();
            _textBox?.SelectAll();
        });
    }

    private void OnTextKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            CommitText();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseTextBox();
        }
    }

    public void CommitText()
    {
        if (_textBox is null || _doc is null)
            return;
        var text = _textBox.Text.TrimEnd();
        var existing = _editingText;
        double fontSize = existing?.FontSize ?? TextSizeDip / Scale;
        var color = ((SolidColorBrush)_textBox.Foreground).Color;
        CloseTextBox();

        if (text.Length == 0)
        {
            if (existing is not null)
                _doc.Remove(existing);
            return;
        }
        var annotation = new TextAnnotation(_textOrigin, text, color, fontSize, TextFont, 1);
        if (existing is not null)
            _doc.Replace(existing, annotation);
        else
            _doc.Add(annotation);
    }

    private void CloseTextBox()
    {
        if (_textBox is null)
            return;
        var box = _textBox;
        _textBox = null;
        _editingText = null;
        _children.Remove(box);
        RenderContent();
        Focus();
    }

    // ---- Visual tree

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        _textBox?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_textBox is not null)
        {
            var at = _view.Transform(_textOrigin);
            // The text box draws its text a couple of pixels in from its left edge.
            _textBox.Arrange(new Rect(new Point(at.X - 2, at.Y), _textBox.DesiredSize));
        }
        return finalSize;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
