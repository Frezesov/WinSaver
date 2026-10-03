using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WinSaver.Controls;
using WinSaver.Editor;

namespace WinSaver.Capture;

/// <summary>Toolbar of the quick edit: the editor's tools without cropping, its colours, undo and the two ways out.</summary>
internal sealed class QuickEditToolbar : Border
{
    private readonly List<RadioButton> _tools = [];
    private readonly Button _undo;
    private readonly Button _redo;

    public QuickEditToolbar(QuickEditor editor)
    {
        SetResourceReference(StyleProperty, "OverlayBar");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        foreach (var tool in EditorTools.All.Where(t => t.Tool != EditorTool.Crop))
        {
            var button = EditorTools.ToolButton(tool, "QuickTool");
            button.Focusable = false;
            button.IsChecked = tool.Tool == editor.Tool;
            button.Checked += (_, _) => editor.SetTool(tool.Tool);
            _tools.Add(button);
            panel.Children.Add(button);
        }

        panel.Children.Add(Divider());
        foreach (var hex in EditorTools.Palette)
        {
            var swatch = EditorTools.ColorSwatch(hex, "QuickColor");
            swatch.Focusable = false;
            swatch.IsChecked = hex == editor.ColorHex;
            swatch.Checked += (_, _) => editor.SetColor(hex);
            panel.Children.Add(swatch);
        }

        panel.Children.Add(Divider());
        _undo = IconButton("", "Отменить", "Отменить (Ctrl + Z)", editor.Undo);
        _redo = IconButton("", "Повторить", "Повторить (Ctrl + Y)", editor.Redo);
        SyncHistory(canUndo: false, canRedo: false);
        panel.Children.Add(_undo);
        panel.Children.Add(_redo);

        panel.Children.Add(Divider());
        panel.Children.Add(IconButton("", "Отменить скриншот", "Отменить скриншот (Esc)", editor.Cancel));
        panel.Children.Add(DoneButton(editor));

        Child = panel;
    }

    public void SyncTool(EditorTool tool)
    {
        foreach (var button in _tools)
            button.IsChecked = (EditorTool)button.Tag == tool;
    }

    public void SyncHistory(bool canUndo, bool canRedo)
    {
        _undo.IsEnabled = canUndo;
        _redo.IsEnabled = canRedo;
    }

    private static Border Divider()
    {
        var divider = new Border();
        divider.SetResourceReference(StyleProperty, "ToolBarDivider");
        return divider;
    }

    private static Button IconButton(string glyph, string name, string hint, Action click)
    {
        var button = new Button { Content = glyph, Width = 36, Height = 36, FontSize = 16, Focusable = false, ToolTip = hint };
        button.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private static Button DoneButton(QuickEditor editor)
    {
        var glyph = new Glyph { Text = "", FontSize = 14, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock { Text = "Готово" });

        var button = new Button
        {
            Content = content,
            Focusable = false,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Скопировать и сохранить (Enter)",
        };
        button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetName(button, "Готово");
        button.Click += (_, _) => editor.Save();
        return button;
    }
}
