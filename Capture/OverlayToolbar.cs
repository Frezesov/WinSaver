using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinSaver.Core;
using CaptureMode = WinSaver.Core.CaptureMode;

namespace WinSaver.Capture;

/// <summary>Mode switch at the top of the screen, like the one in Snipping Tool.</summary>
internal sealed class OverlayToolbar : Border
{
    public OverlayToolbar(CaptureSession session)
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 16, 0, 0);
        SetResourceReference(StyleProperty, "OverlayBar");

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(ModeButton(session, CaptureMode.Rectangle, "", "Прямоугольник"));
        panel.Children.Add(ModeButton(session, CaptureMode.Window, "", "Окно"));
        panel.Children.Add(ModeButton(session, CaptureMode.Screen, "", "Весь экран"));

        var divider = new Border { Width = 1, Margin = new Thickness(4, 6, 4, 6) };
        divider.SetResourceReference(BackgroundProperty, "DividerStrokeColorDefaultBrush");
        panel.Children.Add(divider);

        var close = new Button { Content = "", Width = 40, Height = 36, Focusable = false, ToolTip = "Закрыть (Esc)" };
        close.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(close, "Закрыть");
        close.Click += (_, _) => session.Cancel();
        panel.Children.Add(close);

        Child = panel;
    }

    public void Hide() => Visibility = Visibility.Hidden;

    public void Show() => Visibility = Visibility.Visible;

    // A click on the toolbar's own padding must not start a selection underneath it.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
    }

    private static RadioButton ModeButton(CaptureSession session, CaptureMode mode, string glyph, string name)
    {
        var button = new RadioButton
        {
            Content = glyph,
            GroupName = "CaptureMode",
            IsChecked = session.Mode == mode,
            Focusable = false,
            ToolTip = name,
        };
        button.SetResourceReference(StyleProperty, "ToolToggle");
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Checked += (_, _) => session.SetMode(mode);
        return button;
    }
}
