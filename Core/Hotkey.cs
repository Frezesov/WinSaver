using System.Windows.Input;

namespace WinSaver.Core;

public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public bool IsEmpty => Key == Key.None;

    public bool IsValid => !IsEmpty && (Modifiers != ModifierKeys.None || IsFunctionKey(Key));

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Control) ? Native.MOD_CONTROL : 0) |
        (Modifiers.HasFlag(ModifierKeys.Alt) ? Native.MOD_ALT : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? Native.MOD_SHIFT : 0) |
        (Modifiers.HasFlag(ModifierKeys.Windows) ? Native.MOD_WIN : 0);

    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            if (!IsEmpty) parts.Add(KeyName(Key));
            return parts;
        }
    }

    public override string ToString() => IsEmpty ? "не задано" : string.Join(" + ", Parts);

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    public static bool IsFunctionKey(Key key) => key is >= Key.F1 and <= Key.F24;

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
        Key.Space => "Пробел",
        Key.Enter => "Enter",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.PrintScreen => "PrtSc",
        Key.Scroll => "Scroll Lock",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemTilde => "`",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.Multiply => "Num *",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Divide => "Num /",
        Key.Decimal => "Num .",
        _ => key.ToString(),
    };
}
