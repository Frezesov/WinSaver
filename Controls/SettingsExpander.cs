using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace WinSaver.Controls;

/// <summary>
/// A group of settings in the style of Windows 11 Settings: a card with icon, title, description and a control
/// on the right (<see cref="HeaderContent"/>) that unfolds into nested rows. Being an <see cref="Expander"/>,
/// it reports its expanded state to screen readers.
/// </summary>
public sealed class SettingsExpander : Expander
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsExpander), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsExpander), new PropertyMetadata(null, OnTitleChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsExpander), new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderContentProperty =
        DependencyProperty.Register(nameof(HeaderContent), typeof(object), typeof(SettingsExpander), new PropertyMetadata(null, OnHeaderContentChanged));

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var expander = (SettingsExpander)d;
        AutomationProperties.SetName(expander, e.NewValue as string ?? "");
        NameHeaderControl(expander.HeaderContent, e.NewValue as string);
    }

    private static void OnHeaderContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        NameHeaderControl(e.NewValue, ((SettingsExpander)d).Title);

    // Screen readers announce the switch in the header by the group title.
    private static void NameHeaderControl(object? control, string? title)
    {
        if (control is DependencyObject element && !string.IsNullOrEmpty(title) && string.IsNullOrEmpty(AutomationProperties.GetName(element)))
            AutomationProperties.SetName(element, title);
    }
}
