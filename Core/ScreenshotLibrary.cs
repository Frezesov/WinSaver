using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace WinSaver.Core;

internal sealed record Screenshot(string Path, DateTime Taken);

/// <summary>
/// The screenshot folder. Files are named after the moment they were taken, and that name is also what
/// the recent list and the cleanup go by, so only files that WinSaver itself named are ever touched.
/// </summary>
internal sealed partial class ScreenshotLibrary
{
    private const string TimeFormat = "yyyy-MM-dd HH-mm-ss";

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    [GeneratedRegex(@"^Screenshot (\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2})(?: \(\d+\))?\.png$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinSaver");

    public string Folder { get; set; } = DefaultFolder;

    public static bool TryParse(string fileName, out DateTime taken)
    {
        taken = default;
        var match = NamePattern().Match(fileName);
        return match.Success && DateTime.TryParseExact(match.Groups[1].Value, TimeFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out taken);
    }

    public IReadOnlyList<Screenshot> GetRecent(int count)
    {
        try
        {
            if (!Directory.Exists(Folder))
                return [];
            return Directory.EnumerateFiles(Folder, "Screenshot *.png", SearchOption.TopDirectoryOnly)
                .Select(path => TryParse(Path.GetFileName(path), out var taken) ? new Screenshot(path, taken) : null)
                .OfType<Screenshot>()
                .OrderByDescending(s => s.Taken)
                .ThenByDescending(s => s.Path, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            return [];
        }
    }

    public Screenshot? GetLast() => GetRecent(1).FirstOrDefault();

    /// <summary>Writes the already encoded PNG under a free name; captures within one second get " (2)", " (3)"…</summary>
    public async Task<Screenshot> SaveAsync(byte[] png, DateTime taken)
    {
        var folder = Folder;
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                var path = FreePath(folder, taken);
                WriteAtomic(path, png);
                return new Screenshot(path, taken);
            }).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Deletes screenshots older than <paramref name="keepDays"/> days; returns how many went.</summary>
    public int Cleanup(int keepDays, Func<string, bool> isOpen)
    {
        if (keepDays <= 0)
            return 0;
        var threshold = DateTime.Now.AddDays(-keepDays);
        int deleted = 0;
        try
        {
            if (!Directory.Exists(Folder))
                return 0;
            foreach (var path in Directory.EnumerateFiles(Folder, "Screenshot *.png", SearchOption.TopDirectoryOnly).ToList())
            {
                if (!TryParse(Path.GetFileName(path), out var taken) || taken >= threshold || isOpen(path))
                    continue;
                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ErrorLog.Write(ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
        }
        return deleted;
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    private static string FreePath(string folder, DateTime taken)
    {
        var stem = "Screenshot " + taken.ToString(TimeFormat, CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, stem + ".png");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}).png");
        return path;
    }
}
