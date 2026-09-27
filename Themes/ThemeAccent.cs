using System.Collections;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinSaver.Themes;

// Fluent bakes the Windows accent into ~65 brushes, so overriding SystemAccentColor* has no effect.
// Instead every Fluent brush that uses an accent shade is re-created in the same role from the palette of
// the default Windows 11 blue, so the app looks the same whatever accent the user picked.
internal static class ThemeAccent
{
    private static readonly Color Blue = Hex("#0078D4");
    private static readonly Color BlueLight1 = Hex("#0091F8");
    private static readonly Color BlueLight2 = Hex("#4CC2FF");
    private static readonly Color BlueLight3 = Hex("#99EBFF");
    private static readonly Color BlueDark1 = Hex("#0067C0");
    private static readonly Color BlueDark2 = Hex("#003E92");
    private static readonly Color BlueDark3 = Hex("#001A68");

    private static readonly List<object> AppliedKeys = [];
    private static Application? _app;

    public static void Attach(Application app)
    {
        _app = app;
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Detach() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
            return;
        var dispatcher = _app?.Dispatcher;
        if (dispatcher is null)
            return;
        // Fluent swaps its light/dark dictionary on the same notification; run after it, and once more a bit later.
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Apply);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.ApplicationIdle, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            Apply();
        }, dispatcher);
        timer.Start();
    }

    public static void Apply()
    {
        if (_app is null)
            return;
        var resources = _app.Resources;
        foreach (var key in AppliedKeys)
            resources.Remove(key);
        AppliedKeys.Clear();

        if (SystemParameters.HighContrast)
            return;

        var map = new Dictionary<Color, Color>();
        map.TryAdd(Rgb(SystemColors.AccentColor), Blue);
        map.TryAdd(Rgb(SystemColors.AccentColorLight1), BlueLight1);
        map.TryAdd(Rgb(SystemColors.AccentColorLight2), BlueLight2);
        map.TryAdd(Rgb(SystemColors.AccentColorLight3), BlueLight3);
        map.TryAdd(Rgb(SystemColors.AccentColorDark1), BlueDark1);
        map.TryAdd(Rgb(SystemColors.AccentColorDark2), BlueDark2);
        map.TryAdd(Rgb(SystemColors.AccentColorDark3), BlueDark3);

        var replacements = new Dictionary<object, object>();
        foreach (var dictionary in resources.MergedDictionaries)
            if (dictionary.Contains("AccentFillColorDefaultBrush"))
                Collect(dictionary, map, replacements);

        foreach (var (key, value) in replacements)
        {
            resources[key] = value;
            AppliedKeys.Add(key);
        }
    }

    private static void Collect(ResourceDictionary dictionary, Dictionary<Color, Color> map, Dictionary<object, object> result)
    {
        foreach (var merged in dictionary.MergedDictionaries)
            Collect(merged, map, result);

        foreach (DictionaryEntry entry in dictionary)
        {
            switch (entry.Value)
            {
                case SolidColorBrush brush when map.TryGetValue(Rgb(brush.Color), out var blue):
                    var replacement = new SolidColorBrush(Color.FromArgb(brush.Color.A, blue.R, blue.G, blue.B));
                    replacement.Freeze();
                    result[entry.Key] = replacement;
                    break;
                case Color color when map.TryGetValue(Rgb(color), out var blueColor):
                    result[entry.Key] = Color.FromArgb(color.A, blueColor.R, blueColor.G, blueColor.B);
                    break;
            }
        }
    }

    private static Color Rgb(Color c) => Color.FromRgb(c.R, c.G, c.B);

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
