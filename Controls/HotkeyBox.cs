using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using WinSaver.Core;

namespace WinSaver.Controls;

public sealed class HotkeyBox : Control
{
    private const string CapturePrompt = "Нажмите сочетание…";
    private const string ModifierHint = "Добавьте Ctrl, Alt, Shift или Win";
    private const string EmptyText = "Не задано";

    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(Hotkey), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(default(Hotkey), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((HotkeyBox)d).UpdateDisplay()));

    public static readonly DependencyProperty IsCapturingProperty = DependencyProperty.Register(
        nameof(IsCapturing), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey DisplayPartsKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayParts), typeof(IReadOnlyList<string>), typeof(HotkeyBox), new PropertyMetadata(Array.Empty<string>()));
    public static readonly DependencyProperty DisplayPartsProperty = DisplayPartsKey.DependencyProperty;

    private static readonly DependencyPropertyKey ShowsPlaceholderKey = DependencyProperty.RegisterReadOnly(
        nameof(ShowsPlaceholder), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(true));
    public static readonly DependencyProperty ShowsPlaceholderProperty = ShowsPlaceholderKey.DependencyProperty;

    private static readonly DependencyPropertyKey PlaceholderKey = DependencyProperty.RegisterReadOnly(
        nameof(Placeholder), typeof(string), typeof(HotkeyBox), new PropertyMetadata(EmptyText));
    public static readonly DependencyProperty PlaceholderProperty = PlaceholderKey.DependencyProperty;

    static HotkeyBox()
    {
        FocusableProperty.OverrideMetadata(typeof(HotkeyBox), new FrameworkPropertyMetadata(true));
        KeyboardNavigation.IsTabStopProperty.OverrideMetadata(typeof(HotkeyBox), new FrameworkPropertyMetadata(true));
    }

    public HotkeyBox() => UpdateDisplay();

    public Hotkey Hotkey
    {
        get => (Hotkey)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        set => SetValue(IsCapturingProperty, value);
    }

    public IReadOnlyList<string> DisplayParts => (IReadOnlyList<string>)GetValue(DisplayPartsProperty);

    public string Placeholder => (string)GetValue(PlaceholderProperty);

    /// <summary>True when there are no keys to show, so the placeholder text takes their place.</summary>
    public bool ShowsPlaceholder => (bool)GetValue(ShowsPlaceholderProperty);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        StartCapture();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (!IsCapturing)
        {
            if (key is Key.Enter or Key.Space && Keyboard.Modifiers == ModifierKeys.None)
            {
                StartCapture();
                e.Handled = true;
            }
            return;
        }

        e.Handled = true;
        var modifiers = Keyboard.Modifiers;

        if (Hotkey.IsModifierKey(key))
        {
            ShowPreview(new Hotkey(modifiers, Key.None), CapturePrompt);
            return;
        }
        if (modifiers == ModifierKeys.None && key == Key.Escape)
        {
            EndCapture();
            return;
        }
        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            Hotkey = default;
            EndCapture();
            return;
        }

        var candidate = new Hotkey(modifiers, key);
        if (!candidate.IsValid)
        {
            ShowPreview(new Hotkey(modifiers, Key.None), ModifierHint);
            return;
        }

        Hotkey = candidate;
        EndCapture();
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!IsCapturing)
            return;
        e.Handled = true;
        ShowPreview(new Hotkey(Keyboard.Modifiers, Key.None), CapturePrompt);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        EndCapture();
    }

    private void StartCapture()
    {
        if (IsCapturing)
            return;
        IsCapturing = true;
        ShowPreview(default, CapturePrompt);
    }

    private void EndCapture()
    {
        if (!IsCapturing)
            return;
        IsCapturing = false;
        UpdateDisplay();
    }

    private void ShowPreview(Hotkey preview, string placeholder)
    {
        SetValue(DisplayPartsKey, preview.Parts);
        SetValue(ShowsPlaceholderKey, true);
        SetValue(PlaceholderKey, placeholder);
        AutomationProperties.SetHelpText(this, placeholder);
    }

    private void UpdateDisplay()
    {
        if (IsCapturing)
            return;
        SetValue(DisplayPartsKey, Hotkey.Parts);
        SetValue(ShowsPlaceholderKey, Hotkey.IsEmpty);
        SetValue(PlaceholderKey, EmptyText);
        AutomationProperties.SetHelpText(this, "Сочетание: " + Hotkey + ". Нажмите Enter, чтобы изменить.");
    }
}
