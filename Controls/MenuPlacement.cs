using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using WinSaver.Core;

namespace WinSaver.Controls;

/// <summary>
/// Opens a <see cref="ContextMenu"/> in <c>MenuStyle</c> the way Windows opens its own menus: the visible corner
/// sits at the anchor and the menu opens toward the free side of the work area. WPF's own flipping would leave
/// the transparent shadow margin between the menu and the anchor, so the position is computed here in pixels.
/// </summary>
internal static class MenuPlacement
{
    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>Opens the menu at the mouse pointer, as for a tray icon.</summary>
    public static void OpenAtCursor(ContextMenu menu, UIElement target)
    {
        Native.GetCursorPos(out var pt);
        Open(menu, target, new Native.RECT { Left = pt.X, Top = pt.Y, Right = pt.X, Bottom = pt.Y }, gap: 0);
    }

    /// <summary>Opens the menu under an element (or above it when there is no room), aligned to its left edge.</summary>
    public static void OpenBelow(ContextMenu menu, FrameworkElement anchor)
    {
        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var rect = new Native.RECT
        {
            Left = (int)Math.Round(topLeft.X),
            Top = (int)Math.Round(topLeft.Y),
            Right = (int)Math.Round(bottomRight.X),
            Bottom = (int)Math.Round(bottomRight.Y),
        };
        Open(menu, anchor, rect, gap: 4);
    }

    private static void Open(ContextMenu menu, UIElement target, Native.RECT anchor, int gap)
    {
        var center = new Native.POINT { X = (anchor.Left + anchor.Right) / 2, Y = (anchor.Top + anchor.Bottom) / 2 };
        var monitor = Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        var work = info.rcWork;
        double scale = Native.GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;

        var margin = (Thickness)Application.Current.FindResource("MenuShadowMargin");
        int marginLeft = Px(margin.Left, scale), marginTop = Px(margin.Top, scale);
        int marginX = marginLeft + Px(margin.Right, scale), marginY = marginTop + Px(margin.Bottom, scale);

        // Top-left of the popup window for a popup of the given pixel size.
        (int X, int Y) Locate(int popupWidth, int popupHeight)
        {
            int width = popupWidth - marginX, height = popupHeight - marginY;
            int left = anchor.Left + width <= work.Right ? anchor.Left : anchor.Right - width;
            int top = anchor.Bottom + gap + height <= work.Bottom ? anchor.Bottom + gap : anchor.Top - gap - height;
            left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
            top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
            return (left - marginLeft, top - marginTop);
        }

        // A first guess from a measure outside the popup; it can be a little off, since the menu is not laid out
        // in its window yet, so the real window is moved once it exists. The first frame is transparent (the menu
        // fades in), so the correction is not visible.
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var guess = Locate(Px(menu.DesiredSize.Width, scale), Px(menu.DesiredSize.Height, scale));
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Absolute;
        menu.HorizontalOffset = guess.X / scale;
        menu.VerticalOffset = guess.Y / scale;

        void OnOpened(object? sender, RoutedEventArgs e)
        {
            menu.Opened -= OnOpened;
            if (PresentationSource.FromVisual(menu) is not HwndSource source || !Native.GetWindowRect(source.Handle, out var actual))
                return;
            var (x, y) = Locate(actual.Width, actual.Height);
            if (actual.Left != x || actual.Top != y)
                Native.SetWindowPos(source.Handle, IntPtr.Zero, x, y, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        menu.Opened += OnOpened;
        menu.IsOpen = true;
    }

    private static int Px(double dips, double scale) => (int)Math.Round(dips * scale);
}
