using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WinSaver.Capture;

/// <summary>Dims the frozen screen and cuts the highlighted area out of the dimming, with its size next to it.</summary>
internal sealed class OverlaySurface : FrameworkElement
{
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)));
    private static readonly Brush LabelBackground = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)));
    private static readonly Brush Outline = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));

    private readonly CaptureSession _session;
    private readonly MonitorArea _monitor;

    public OverlaySurface(CaptureSession session, MonitorArea monitor)
    {
        _session = session;
        _monitor = monitor;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        if (_session.Highlight is not { } highlight)
        {
            dc.DrawRectangle(Dim, null, full);
            return;
        }

        var local = new Rect(
            (highlight.X - _monitor.Bounds.X) / scale,
            (highlight.Y - _monitor.Bounds.Y) / scale,
            highlight.Width / scale,
            highlight.Height / scale);

        dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(full), new RectangleGeometry(local)));

        // A one-pixel line drawn just outside the area, snapped to device pixels.
        double px = 1 / scale;
        var border = new Rect(local.X - px / 2, local.Y - px / 2, local.Width + px, local.Height + px);
        var guidelines = new GuidelineSet([border.Left - px / 2, border.Right + px / 2], [border.Top - px / 2, border.Bottom + px / 2]);
        dc.PushGuidelineSet(guidelines);
        dc.DrawRectangle(null, new Pen(Outline, px), border);
        dc.Pop();

        if (_session.LabelMonitor == _monitor && !_session.IsEditing)
            DrawSize(dc, highlight, local, full);
    }

    private void DrawSize(DrawingContext dc, PixelRect area, Rect local, Rect full)
    {
        var text = new FormattedText($"{area.Width} × {area.Height}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TextElementFontFamily(), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var size = new Size(text.Width + 16, text.Height + 8);
        double x = Math.Clamp(local.Left, 4, Math.Max(4, full.Width - size.Width - 4));
        double y = local.Bottom + 8;
        if (y + size.Height > full.Height - 4)
            y = local.Bottom - size.Height - 8;
        if (y < 4)
            y = local.Top + 8;

        var box = new Rect(new Point(x, y), size);
        dc.DrawRoundedRectangle(LabelBackground, null, box, 4, 4);
        dc.DrawText(text, new Point(box.X + 8, box.Y + 4));
    }

    private FontFamily TextElementFontFamily() =>
        TryFindResource("AppFontFamily") as FontFamily ?? new FontFamily("Segoe UI");

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
