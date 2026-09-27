using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinSaver.Core;

/// <summary>
/// Catches the capture shortcuts with a low-level keyboard hook: Win + Shift + S, optionally Print Screen and the
/// user's own combination. Low-level hooks run before the shell's own hotkeys, so a swallowed key never opens
/// Snipping Tool or whatever else Windows keeps on that combination.
/// The hook lives on its own thread with a message loop: a slow UI thread would otherwise delay every
/// keystroke in the system, and Windows silently drops hooks that time out.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private sealed record Shortcut(int Vk, ModifierKeys Modifiers);

    private static readonly Shortcut Snipping = new(Native.VK_S, ModifierKeys.Windows | ModifierKeys.Shift);
    private static readonly Shortcut PrintScreen = new(Native.VK_SNAPSHOT, ModifierKeys.None);

    // Marks keystrokes sent by the hook itself so it lets them through.
    private static readonly IntPtr OwnInput = new(0x57534156);
    // An unassigned key: pressing it while Win or Alt is held keeps the Start menu or the window's menu bar
    // from opening when the modifier is released.
    private const ushort DummyKey = 0xFF;

    private readonly Dispatcher _dispatcher;
    private readonly Native.LowLevelKeyboardProc _proc;
    private Thread? _thread;
    private uint _threadId;
    private volatile Shortcut[] _shortcuts = [];

    // Touched by the hook thread only.
    private int _heldKey;

    public event Action? Pressed;

    public KeyboardHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = HookProc;
    }

    public void Configure(bool snipping, bool printScreen, Hotkey custom)
    {
        var shortcuts = new List<Shortcut>(3);
        if (snipping)
            shortcuts.Add(Snipping);
        if (printScreen)
            shortcuts.Add(PrintScreen);
        if (custom.IsValid)
            shortcuts.Add(new((int)custom.VirtualKey, custom.Modifiers));
        _shortcuts = [.. shortcuts];

        if (shortcuts.Count > 0)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        if (_thread is not null)
            return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "WinSaver keyboard hook" };
        _thread.Start();
        ready.Wait();
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        // Creates the thread's message queue before anyone may post WM_QUIT to it.
        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, Native.PM_NOREMOVE);
        var hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
            ErrorLog.Write(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed"));
        ready.Set();

        while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
        }

        if (hook != IntPtr.Zero)
            Native.UnhookWindowsHookEx(hook);
    }

    private void Stop()
    {
        if (_thread is null)
            return;
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
        _heldKey = 0;
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != OwnInput && Swallow((int)info.vkCode, (int)wParam))
                return 1;
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private bool Swallow(int vk, int message)
    {
        bool down = message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        // Repeats and the release of a key that started a capture go the same way as its press.
        if (vk == _heldKey)
        {
            if (!down)
                _heldKey = 0;
            return true;
        }
        if (!down)
            return false;

        var modifiers = HeldModifiers();
        foreach (var shortcut in _shortcuts)
        {
            if (shortcut.Vk != vk || shortcut.Modifiers != modifiers)
                continue;
            _heldKey = vk;
            if ((modifiers & (ModifierKeys.Windows | ModifierKeys.Alt)) != 0)
                PressDummyKey();
            Raise();
            return true;
        }
        return false;
    }

    private static ModifierKeys HeldModifiers()
    {
        var modifiers = ModifierKeys.None;
        if (Native.IsKeyDown(Native.VK_CONTROL))
            modifiers |= ModifierKeys.Control;
        if (Native.IsKeyDown(Native.VK_MENU))
            modifiers |= ModifierKeys.Alt;
        if (Native.IsKeyDown(Native.VK_SHIFT))
            modifiers |= ModifierKeys.Shift;
        if (Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN))
            modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    private static void PressDummyKey()
    {
        var inputs = new Native.INPUT[2];
        inputs[0].type = inputs[1].type = Native.INPUT_KEYBOARD;
        inputs[0].u.ki = new Native.KEYBDINPUT { wVk = DummyKey, dwExtraInfo = OwnInput };
        inputs[1].u.ki = new Native.KEYBDINPUT { wVk = DummyKey, dwFlags = Native.KEYEVENTF_KEYUP, dwExtraInfo = OwnInput };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    private void Raise() => _dispatcher.BeginInvoke(() => Pressed?.Invoke());

    public void Dispose() => Stop();
}
