using System.Globalization;

namespace WinSaver.Core;

/// <summary>Screenshot times the way people say them: "сегодня, 14:32", "вчера, 18:01", "25 сентября, 09:15".</summary>
internal static class When
{
    private static readonly CultureInfo Russian = new("ru-RU");

    public static string Format(DateTime moment, bool seconds = false)
    {
        var time = moment.ToString(seconds ? "HH:mm:ss" : "HH:mm", Russian);
        var today = DateTime.Today;
        if (moment.Date == today)
            return $"сегодня, {time}";
        if (moment.Date == today.AddDays(-1))
            return $"вчера, {time}";
        var date = moment.ToString(moment.Year == today.Year ? "d MMMM" : "d MMMM yyyy", Russian);
        return $"{date}, {time}";
    }

    public static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], Russian) + text[1..];
}
