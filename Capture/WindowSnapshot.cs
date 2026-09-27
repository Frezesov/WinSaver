using System.Runtime.InteropServices;
using WinSaver.Core;

namespace WinSaver.Capture;

internal static class WindowSnapshot
{
    /// <summary>
    /// Bounds of the windows on screen, topmost first, taken together with the screenshot so that
    /// "Window" mode picks what the frozen image shows rather than what is there now.
    /// </summary>
    public static List<PixelRect> Collect()
    {
        var windows = new List<PixelRect>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (IsOnScreen(hwnd) && TryGetBounds(hwnd, out var bounds))
                windows.Add(bounds);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool IsOnScreen(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
            return false;
        // Suspended Store apps and windows on other virtual desktops are "visible" but cloaked.
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        // Click-through overlays cover everything but are not something to pick.
        if ((Native.GetExStyle(hwnd) & Native.WS_EX_TRANSPARENT) != 0)
            return false;
        return Native.GetClassName(hwnd) is not ("Progman" or "WorkerW");
    }

    // The extended frame leaves out the invisible resize borders Windows 10+ puts around windows.
    private static bool TryGetBounds(IntPtr hwnd, out PixelRect bounds)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT rect, Marshal.SizeOf<Native.RECT>()) != 0
            && !Native.GetWindowRect(hwnd, out rect))
        {
            bounds = default;
            return false;
        }
        bounds = PixelRect.FromRect(rect);
        return bounds.Width >= 8 && bounds.Height >= 8;
    }
}
