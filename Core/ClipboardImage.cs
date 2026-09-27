using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSaver.Core;

/// <summary>
/// Puts an image on the clipboard as a DIB for classic apps and as PNG for browsers and messengers, in one go,
/// so it stays a single entry in the clipboard history (Win + V). The Win32 clipboard is used directly because
/// WPF's DataObject.SetImage leaves a GDI bitmap of the whole picture behind on every copy.
/// </summary>
internal static class ClipboardImage
{
    private const uint CF_DIB = 8;
    private const int Attempts = 10;

    private static readonly uint PngFormat = Native.RegisterClipboardFormat("PNG");
    private static HwndSource? _owner;

    public static bool Set(BitmapSource image, byte[] png)
    {
        // Without an owner window EmptyClipboard leaves the clipboard ownerless and SetClipboardData fails.
        _owner ??= new HwndSource(new HwndSourceParameters("WinSaver.Clipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        var dib = BuildDib(image);

        // Another app may hold the clipboard open for a moment, e.g. a clipboard manager reading the previous copy.
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            if (!Native.OpenClipboard(_owner.Handle))
            {
                Thread.Sleep(50);
                continue;
            }
            try
            {
                Native.EmptyClipboard();
                bool dibSet = Put(CF_DIB, dib);
                bool pngSet = PngFormat != 0 && Put(PngFormat, png);
                return dibSet || pngSet;
            }
            finally
            {
                Native.CloseClipboard();
            }
        }
        ErrorLog.Write(new Win32Exception(Marshal.GetLastWin32Error(), "The clipboard stayed busy"));
        return false;
    }

    // Once SetClipboardData succeeds the memory belongs to the system and must not be freed here.
    private static bool Put(uint format, byte[] data)
    {
        IntPtr handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero)
            return false;
        IntPtr target = Native.GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            Native.GlobalFree(handle);
            return false;
        }
        Marshal.Copy(data, 0, target, data.Length);
        Native.GlobalUnlock(handle);
        if (Native.SetClipboardData(format, handle) != IntPtr.Zero)
            return true;
        Native.GlobalFree(handle);
        return false;
    }

    // 24-bit and bottom-up, the form every app reads; with 32 bits some apps take the unused byte for transparency.
    private static byte[] BuildDib(BitmapSource image)
    {
        BitmapSource source = image.Format == PixelFormats.Bgr24 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        int stride = (width * 3 + 3) & ~3;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        const int headerSize = 40;
        var dib = new byte[headerSize + pixels.Length];
        var header = dib.AsSpan(0, headerSize);
        BitConverter.TryWriteBytes(header[0..], headerSize);
        BitConverter.TryWriteBytes(header[4..], width);
        BitConverter.TryWriteBytes(header[8..], height);
        BitConverter.TryWriteBytes(header[12..], (short)1);
        BitConverter.TryWriteBytes(header[14..], (short)24);
        BitConverter.TryWriteBytes(header[20..], pixels.Length);
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * stride, dib, headerSize + (height - 1 - y) * stride, stride);
        return dib;
    }
}
