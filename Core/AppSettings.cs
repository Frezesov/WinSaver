using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using WinSaver.Editor;

namespace WinSaver.Core;

public enum CaptureMode { Rectangle, Window, Screen }

public sealed class AppSettings
{
    public bool ReplaceSnippingHotkey { get; set; } = true;
    public bool InterceptPrintScreen { get; set; }
    public ModifierKeys HotkeyModifiers { get; set; }
    public Key HotkeyKey { get; set; }

    /// <summary>Where screenshots go; empty means <see cref="ScreenshotLibrary.DefaultFolder"/>.</summary>
    public string? SaveFolder { get; set; }

    /// <summary>Screenshots older than this many days are deleted; 0 keeps them forever.</summary>
    public int KeepDays { get; set; }

    public CaptureMode CaptureMode { get; set; } = CaptureMode.Rectangle;

    /// <summary>Draw on the picked area right on the frozen screen before the screenshot is kept.</summary>
    public bool QuickEdit { get; set; }

    public EditorTool EditorTool { get; set; } = EditorTool.Arrow;
    public string EditorColor { get; set; } = "#FF3B30";
    public double EditorSize { get; set; } = 4;

    public bool CheckForUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public string? LatestVersion { get; set; }
}

/// <summary>
/// The folder for settings and the error log. Portable: next to the exe when that folder is writable,
/// otherwise in %APPDATA%\WinSaver.
/// </summary>
internal static class AppPaths
{
    public const string SettingsFileName = "settings.json";

    public static string DataFolder { get; } = Resolve();

    private static string Resolve()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSaver");

        if (File.Exists(Path.Combine(exeDir, SettingsFileName))) return exeDir;
        if (File.Exists(Path.Combine(roaming, SettingsFileName))) return roaming;
        return CanWrite(exeDir) ? exeDir : roaming;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = Path.Combine(AppPaths.DataFolder, AppPaths.SettingsFileName);

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
        }
    }
}
