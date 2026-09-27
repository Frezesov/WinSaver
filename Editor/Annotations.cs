using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSaver.Editor;

public enum EditorTool { Pen, Marker, Arrow, Rectangle, Ellipse, Text, Pixelate, Crop, Eraser }

/// <summary>Something drawn over the screenshot. Coordinates and sizes are in pixels of the image.</summary>
internal abstract class Annotation
{
    public abstract void Render(DrawingContext dc);

    public abstract bool HitTest(Point point, double tolerance);

    protected static Pen RoundPen(Brush brush, double width)
    {
        var pen = new Pen(brush, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    protected static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

internal enum InkKind { Pen, Marker, Arrow }

/// <summary>
/// A freehand line: pen, marker or arrow. The path is smoothed with quadratic curves through the midpoints of
/// the recorded segments. A marker is drawn as one stroke, so where it crosses itself it does not get darker.
/// An arrow gets its head from the direction of the last stretch of the line, not of the last jittery segment.
/// </summary>
internal sealed class InkAnnotation : Annotation
{
    private const double MarkerOpacity = 0.45;
    private const double HeadAngle = 28 * Math.PI / 180;

    private readonly Pen _pen;
    private readonly Geometry _path;
    private readonly Geometry? _head;
    private readonly Point? _dot;
    private readonly Brush _brush;
    private readonly double _width;

    public InkAnnotation(IReadOnlyList<Point> points, Color color, double width, InkKind kind)
    {
        _width = width;
        _brush = Solid(kind == InkKind.Marker ? Color.FromArgb((byte)(255 * MarkerOpacity), color.R, color.G, color.B) : color);
        _pen = RoundPen(_brush, width);

        if (points.Count == 1)
            _dot = points[0];
        _path = BuildPath(points);
        if (kind == InkKind.Arrow)
            _head = BuildHead(points, width);
    }

    public override void Render(DrawingContext dc)
    {
        if (_dot is { } dot)
        {
            dc.DrawEllipse(_brush, null, dot, _width / 2, _width / 2);
            return;
        }
        dc.DrawGeometry(null, _pen, _path);
        if (_head is not null)
            dc.DrawGeometry(null, _pen, _head);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        if (_dot is { } dot)
            return (point - dot).Length <= _width / 2 + tolerance;
        var pen = new Pen(Brushes.Black, _width + tolerance * 2);
        return _path.StrokeContains(pen, point) || (_head?.StrokeContains(pen, point) ?? false);
    }

    private static Geometry BuildPath(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            if (points.Count == 1)
            {
                ctx.LineTo(points[0], true, true);
            }
            else
            {
                for (int i = 1; i < points.Count - 1; i++)
                {
                    var mid = new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
                    ctx.QuadraticBezierTo(points[i], mid, true, true);
                }
                ctx.LineTo(points[^1], true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry? BuildHead(IReadOnlyList<Point> points, double width)
    {
        double length = Math.Max(width * 3.2, 12);
        var tip = points[^1];
        var from = tip;
        double walked = 0;
        for (int i = points.Count - 2; i >= 0; i--)
        {
            walked += (points[i + 1] - points[i]).Length;
            from = points[i];
            if (walked >= length * 0.8)
                break;
        }
        var direction = tip - from;
        if (direction.Length < 0.5)
            return null;
        direction.Normalize();

        var left = tip - Rotate(direction, HeadAngle) * length;
        var right = tip - Rotate(direction, -HeadAngle) * length;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(left, isFilled: false, isClosed: false);
            ctx.LineTo(tip, true, true);
            ctx.LineTo(right, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Vector Rotate(Vector v, double angle)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }
}

internal sealed class ShapeAnnotation : Annotation
{
    private readonly Rect _bounds;
    private readonly bool _ellipse;
    private readonly Pen _pen;
    private readonly Geometry _geometry;

    public ShapeAnnotation(Rect bounds, Color color, double width, bool ellipse)
    {
        _bounds = bounds;
        _ellipse = ellipse;
        _pen = RoundPen(Solid(color), width);
        _geometry = ellipse
            ? new EllipseGeometry(bounds)
            : new RectangleGeometry(bounds, Math.Min(width, bounds.Width / 2), Math.Min(width, bounds.Height / 2));
        _geometry.Freeze();
    }

    public override void Render(DrawingContext dc) => dc.DrawGeometry(null, _pen, _geometry);

    public override bool HitTest(Point point, double tolerance) =>
        _geometry.StrokeContains(new Pen(Brushes.Black, _pen.Thickness + tolerance * 2), point)
        || (!_ellipse && _bounds.Width < tolerance * 4 && _bounds.Contains(point));
}

/// <summary>Text with an outline in a contrasting colour, so it stays readable on any background.</summary>
internal sealed class TextAnnotation : Annotation
{
    private readonly Geometry _geometry;
    private readonly Brush _fill;
    private readonly Pen _outline;

    public TextAnnotation(Point origin, string text, Color color, double fontSize, FontFamily font, double pixelsPerDip)
    {
        Origin = origin;
        Text = text;
        Color = color;
        FontSize = fontSize;

        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            fontSize, Brushes.Black, pixelsPerDip);
        _geometry = formatted.BuildGeometry(origin);
        _geometry.Freeze();
        Bounds = new Rect(origin, new Size(Math.Max(formatted.WidthIncludingTrailingWhitespace, 1), formatted.Height));

        _fill = Solid(color);
        double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        var outline = luminance > 0.6 ? Color.FromArgb(0xB0, 0, 0, 0) : Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF);
        _outline = RoundPen(Solid(outline), Math.Max(2, fontSize * 0.14));
    }

    public Point Origin { get; }
    public string Text { get; }
    public Color Color { get; }
    public double FontSize { get; }
    public Rect Bounds { get; }

    public override void Render(DrawingContext dc)
    {
        dc.DrawGeometry(null, _outline, _geometry);
        dc.DrawGeometry(_fill, null, _geometry);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var area = Bounds;
        area.Inflate(tolerance, tolerance);
        return area.Contains(point);
    }
}

/// <summary>
/// Hides part of the picture behind large blocks of its average colours. The blocks are baked into a bitmap
/// at full size, so the result is the same on screen, in the clipboard and in the file.
/// </summary>
internal sealed class PixelateAnnotation : Annotation
{
    private readonly Rect _area;
    private readonly BitmapSource _mosaic;

    public PixelateAnnotation(BitmapSource image, Int32Rect area)
    {
        _area = new Rect(area.X, area.Y, area.Width, area.Height);
        _mosaic = BuildMosaic(image, area);
    }

    public override void Render(DrawingContext dc) => dc.DrawImage(_mosaic, _area);

    public override bool HitTest(Point point, double tolerance) => _area.Contains(point);

    private static BitmapSource BuildMosaic(BitmapSource image, Int32Rect area)
    {
        int width = area.Width, height = area.Height, stride = width * 4;
        var pixels = new byte[stride * height];
        image.CopyPixels(area, pixels, stride, 0);

        int block = Math.Clamp((int)Math.Round(Math.Min(width, height) / 5.0), 8, 40);
        for (int by = 0; by < height; by += block)
        {
            for (int bx = 0; bx < width; bx += block)
            {
                int w = Math.Min(block, width - bx), h = Math.Min(block, height - by);
                long b = 0, g = 0, r = 0;
                for (int y = by; y < by + h; y++)
                {
                    int row = y * stride;
                    for (int x = bx; x < bx + w; x++)
                    {
                        int i = row + x * 4;
                        b += pixels[i];
                        g += pixels[i + 1];
                        r += pixels[i + 2];
                    }
                }
                int count = w * h;
                byte ab = (byte)(b / count), ag = (byte)(g / count), ar = (byte)(r / count);
                for (int y = by; y < by + h; y++)
                {
                    int row = y * stride;
                    for (int x = bx; x < bx + w; x++)
                    {
                        int i = row + x * 4;
                        pixels[i] = ab;
                        pixels[i + 1] = ag;
                        pixels[i + 2] = ar;
                        pixels[i + 3] = 0xFF;
                    }
                }
            }
        }

        var mosaic = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        mosaic.Freeze();
        return mosaic;
    }
}
