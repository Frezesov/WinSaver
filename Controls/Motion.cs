using System.Windows;

namespace WinSaver.Controls;

/// <summary>
/// Whether the app animates at all. Everything falls back to instant changes when "Animation effects" is off in Windows;
/// XAML animations read their durations from the Motion* resources, which App zeroes in that case.
/// </summary>
internal static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;
}
