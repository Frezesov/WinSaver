using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinSaver.Editor;

/// <summary>A tool as its button shows it. Blur has no font glyph and gets a drawn mosaic instead.</summary>
internal sealed record ToolInfo(EditorTool Tool, string? Glyph, string Name, string Hint, Key Key);

/// <summary>Tools and colours of the editor, shared by the editor window and the quick edit on the capture overlay.</summary>
internal static class EditorTools
{
    public static IReadOnlyList<ToolInfo> All { get; } =
    [
        new(EditorTool.Pen, "", "Перо", "Перо (P)", Key.P),
        new(EditorTool.Marker, "", "Маркер", "Маркер (M)", Key.M),
        new(EditorTool.Arrow, "", "Стрелка", "Стрелка (A). Рисуйте линию — наконечник появится сам, с Shift линия прямая", Key.A),
        new(EditorTool.Rectangle, "", "Прямоугольник", "Прямоугольник (R). С Shift — квадрат", Key.R),
        new(EditorTool.Ellipse, "", "Овал", "Овал (O). С Shift — круг", Key.O),
        new(EditorTool.Text, "", "Текст", "Текст (T). Enter — готово, Shift + Enter — новая строка", Key.T),
        new(EditorTool.Pixelate, null, "Размытие", "Размытие (B). Скрывает личные данные мозаикой", Key.B),
        new(EditorTool.Crop, "", "Обрезка", "Обрезка (C). Enter — применить, Esc — отмена", Key.C),
        new(EditorTool.Eraser, "", "Ластик", "Ластик (E). Стирает нарисованное целиком", Key.E),
    ];

    public static IReadOnlyList<string> Palette { get; } =
        ["#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#0A84FF", "#AF52DE", "#FFFFFF", "#000000"];

    /// <summary>Line thickness range, in DIPs.</summary>
    public const double MinSize = 1;
    public const double MaxSize = 16;

    public static EditorTool? ForKey(Key key) => All.FirstOrDefault(t => t.Key == key)?.Tool;

    public static string ColorName(string hex) => hex switch
    {
        "#FF3B30" => "Красный",
        "#FF9500" => "Оранжевый",
        "#FFCC00" => "Жёлтый",
        "#34C759" => "Зелёный",
        "#0A84FF" => "Синий",
        "#AF52DE" => "Фиолетовый",
        "#FFFFFF" => "Белый",
        _ => "Чёрный",
    };

    public static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return Colors.Red;
        }
    }

    /// <summary>A toolbar toggle for the tool, with the tool in <see cref="FrameworkElement.Tag"/>.</summary>
    public static RadioButton ToolButton(ToolInfo tool, string group)
    {
        var button = new RadioButton { GroupName = group, Tag = tool.Tool, ToolTip = tool.Hint };
        button.SetResourceReference(FrameworkElement.StyleProperty, "ToolToggle");
        button.Content = tool.Glyph ?? (object)MosaicIcon(button);
        AutomationProperties.SetName(button, tool.Name);
        return button;
    }

    /// <summary>A colour dot for a toolbar, with the colour as "#RRGGBB" in <see cref="FrameworkElement.Tag"/>.</summary>
    public static RadioButton ColorSwatch(string hex, string group)
    {
        var swatch = new RadioButton
        {
            GroupName = group,
            Background = new SolidColorBrush(ParseColor(hex)),
            Tag = hex,
            ToolTip = ColorName(hex),
        };
        swatch.SetResourceReference(FrameworkElement.StyleProperty, "ColorSwatch");
        AutomationProperties.SetName(swatch, ColorName(hex));
        return swatch;
    }

    // Takes the button's foreground, so it turns to the accent colour along with the glyphs when picked.
    private static Path MosaicIcon(RadioButton owner)
    {
        var icon = new Path { Width = 16, Height = 16, Stretch = Stretch.Uniform };
        icon.SetResourceReference(Path.DataProperty, "MosaicIcon");
        icon.SetBinding(Shape.FillProperty, new Binding(nameof(Control.Foreground)) { Source = owner });
        return icon;
    }
}
