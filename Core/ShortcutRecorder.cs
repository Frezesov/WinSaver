using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinSaver.Core;

/// <summary>
/// Takes every keystroke system-wide while a shortcut is being typed and reports it instead. Without it Win alone
/// opens the Start menu and the shell grabs its own combinations, such as Win + Q, before the window sees them.
/// A key pressed while recording also has its release swallowed, even after recording stops, so the system never
/// gets a release without the press.
/// </summary>
internal sealed class ShortcutRecorder
{
    // Low-level hooks report the left and right modifier keys separately.
    private static readonly (int Vk, ModifierKeys Modifier)[] Modifiers =
    [
        (0xA2, ModifierKeys.Control), (0xA3, ModifierKeys.Control),
        (0xA4, ModifierKeys.Alt), (0xA5, ModifierKeys.Alt),
        (0xA0, ModifierKeys.Shift), (0xA1, ModifierKeys.Shift),
        (Native.VK_LWIN, ModifierKeys.Windows), (Native.VK_RWIN, ModifierKeys.Windows),
    ];

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Native.LowLevelKeyboardProc _proc;
    private readonly HashSet<int> _pressed = [];
    private IntPtr _hook;
    private IntPtr _window;

    /// <summary>Raised for every key press and release while recording, with the modifiers held after it.</summary>
    public event Action<Key, ModifierKeys, bool>? KeyChanged;

    public ShortcutRecorder() => _proc = HookProc;

    public bool IsRecording { get; private set; }

    /// <summary>
    /// Starts recording on the calling thread, which must pump messages. Keys are taken only while
    /// <paramref name="window"/> is the foreground window, so switching away gives the keyboard back at once.
    /// </summary>
    public bool Start(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return false;
        _window = window;
        if (_hook == IntPtr.Zero)
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        IsRecording = _hook != IntPtr.Zero;
        return IsRecording;
    }

    public void Stop()
    {
        IsRecording = false;
        RemoveHookWhenIdle();
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (Take((int)info.vkCode, (int)wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN))
                return 1;
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private bool Take(int vk, bool down)
    {
        if (!down && _pressed.Remove(vk))
        {
            if (IsRecording)
                Report(vk, down);
            else
                _dispatcher.BeginInvoke(RemoveHookWhenIdle);
            return true;
        }

        if (!IsRecording || Native.GetForegroundWindow() != _window)
        {
            // The system sees this press, so it must see the release too.
            if (down && _pressed.Remove(vk))
                _dispatcher.BeginInvoke(RemoveHookWhenIdle);
            return false;
        }

        if (down)
            _pressed.Add(vk);
        Report(vk, down);
        // A key held since before recording started is released normally, or the system would think it still held.
        return down;
    }

    private void Report(int vk, bool down)
    {
        var modifiers = ModifierKeys.None;
        foreach (var (key, modifier) in Modifiers)
        {
            // Swallowed presses never reach the system key state, which only knows the keys held before recording.
            bool held = key == vk ? down : _pressed.Contains(key) || Native.IsKeyDown(key);
            if (held)
                modifiers |= modifier;
        }
        var wpfKey = KeyInterop.KeyFromVirtualKey(vk);
        _dispatcher.BeginInvoke(() => KeyChanged?.Invoke(wpfKey, modifiers, down));
    }

    private void RemoveHookWhenIdle()
    {
        if (IsRecording || _pressed.Count > 0 || _hook == IntPtr.Zero)
            return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
