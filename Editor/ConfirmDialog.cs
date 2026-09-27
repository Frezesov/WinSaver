using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace WinSaver.Editor;

/// <summary>A small question in the style of Windows 11 dialogs: text on top, buttons in a footer.</summary>
internal sealed class ConfirmDialog : Window
{
    public enum Result { Cancel, Primary, Secondary }

    private Result _result = Result.Cancel;

    private ConfirmDialog(Window owner, string title, string message, string primary, string? secondary)
    {
        Owner = owner;
        Title = "WinSaver";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 14;
        SetResourceReference(FontFamilyProperty, "AppFontFamily");

        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), LineHeight = 20 };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var text = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        text.Children.Add(heading);
        text.Children.Add(body);

        var buttons = new UniformGrid { Rows = 1 };
        buttons.Children.Add(MakeButton(primary, Result.Primary, accent: true, isDefault: true, isCancel: secondary is null));
        if (secondary is not null)
        {
            buttons.Children.Add(MakeButton(secondary, Result.Secondary, accent: false, isDefault: false, isCancel: false));
            buttons.Children.Add(MakeButton("Отмена", Result.Cancel, accent: false, isDefault: false, isCancel: true));
        }
        var footer = new Border { Padding = new Thickness(24, 20, 16, 20), BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons };
        footer.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(text);
        Content = root;
    }

    public static Result Show(Window owner, string title, string message, string primary, string? secondary)
    {
        var dialog = new ConfirmDialog(owner, title, message, primary, secondary);
        dialog.ShowDialog();
        return dialog._result;
    }

    private Button MakeButton(string text, Result result, bool accent, bool isDefault, bool isCancel)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        if (accent)
            button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        button.Click += (_, _) =>
        {
            _result = result;
            Close();
        };
        return button;
    }
}
