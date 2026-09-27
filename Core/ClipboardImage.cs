using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinSaver.Core;

internal static class ClipboardImage
{
    /// <summary>
    /// Puts the image on the clipboard once, as a bitmap for classic apps and as PNG for browsers and messengers.
    /// Setting it in one go keeps it a single entry in the clipboard history (Win + V).
    /// </summary>
    public static bool Set(BitmapSource image, byte[] png)
    {
        try
        {
            var data = new DataObject();
            data.SetImage(image);
            data.SetData("PNG", new MemoryStream(png), false);
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
        {
            ErrorLog.Write(ex);
            return false;
        }
    }
}
