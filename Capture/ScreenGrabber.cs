using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSaver.Core;

namespace WinSaver.Capture;

internal sealed record MonitorArea(PixelRect Bounds, double Scale);

internal static class ScreenGrabber
{
    public static PixelRect VirtualScreen => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    public static List<MonitorArea> GetMonitors()
    {
        var monitors = new List<MonitorArea>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(monitor, ref info))
                monitors.Add(new MonitorArea(PixelRect.FromRect(info.rcMonitor), Native.GetMonitorScale(monitor)));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    /// <summary>Copies what is on screen in <paramref name="area"/>, in physical pixels, into a frozen bitmap.</summary>
    public static BitmapSource Capture(PixelRect area)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr memory = Native.CreateCompatibleDC(screen);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = area.Width,
            biHeight = -area.Height,
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = Native.CreateDIBSection(screen, ref header, Native.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection failed");
            IntPtr old = Native.SelectObject(memory, dib);
            // CAPTUREBLT brings in layered windows such as menus and tooltips.
            Native.BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, Native.SRCCOPY | Native.CAPTUREBLT);
            Native.SelectObject(memory, old);

            int stride = area.Width * 4;
            var image = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, bits, stride * area.Height, stride);
            image.Freeze();
            return image;
        }
        finally
        {
            if (dib != IntPtr.Zero)
                Native.DeleteObject(dib);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>A standalone copy of part of <paramref name="source"/>, so the full-desktop bitmap can be released.</summary>
    public static BitmapSource CopyRegion(BitmapSource source, PixelRect region)
    {
        var cropped = new CroppedBitmap(source, region.ToInt32Rect());
        int stride = region.Width * 4;
        var pixels = new byte[stride * region.Height];
        cropped.CopyPixels(pixels, stride, 0);
        var image = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        image.Freeze();
        return image;
    }
}
