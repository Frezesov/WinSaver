using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinSaver.Core;

/// <summary>
/// Takes Win + Shift + S (and optionally Print Screen) away from Windows with a low-level keyboard hook.
/// Low-level hooks run before the shell's own hotkeys, so the swallowed keys never open Snipping Tool.
/// The hook lives on its own thread with a message loop: a slow UI thread would otherwise delay every
/// keystroke in the system, and Windows silently drops hooks that time out.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    // Marks keystrokes sent by the hook itself so it lets them through.
    private static readonly IntPtr OwnInput = new(0x57534156);
    // An unassigned key: pressing it while Win is held keeps the Start menu from opening when Win is released.
    private const ushort DummyKey = 0xFF;

    private readonly Dispatcher _dispatcher;
    private readonly Native.LowLevelKeyboardProc _proc;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _snipping;
    private volatile bool _printScreen;

    // Touched by the hook thread only.
    private bool _holdingS;
    private bool _holdingPrint;

    public event Action? Pressed;

    public KeyboardHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = HookProc;
    }

    public void Configure(bool snipping, bool printScreen)
    {
        _snipping = snipping;
        _printScreen = printScreen;
        if (snipping || printScreen)
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
        _holdingS = _holdingPrint = false;
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
        bool up = message is Native.WM_KEYUP or Native.WM_SYSKEYUP;

        if (vk == Native.VK_S)
        {
            if (up && _holdingS)
            {
                _holdingS = false;
                return true;
            }
            if (down && _holdingS)
                return true;
            if (down && _snipping && IsWinShift())
            {
                _holdingS = true;
                PressDummyKey();
                Raise();
                return true;
            }
        }
        else if (vk == Native.VK_SNAPSHOT)
        {
            if (up && _holdingPrint)
            {
                _holdingPrint = false;
                return true;
            }
            if (down && _holdingPrint)
                return true;
            if (down && _printScreen && NoModifiers())
            {
                _holdingPrint = true;
                Raise();
                return true;
            }
        }
        return false;
    }

    private static bool IsWinShift() =>
        (Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN))
        && Native.IsKeyDown(Native.VK_SHIFT)
        && !Native.IsKeyDown(Native.VK_CONTROL)
        && !Native.IsKeyDown(Native.VK_MENU);

    private static bool NoModifiers() =>
        !Native.IsKeyDown(Native.VK_LWIN) && !Native.IsKeyDown(Native.VK_RWIN)
        && !Native.IsKeyDown(Native.VK_SHIFT) && !Native.IsKeyDown(Native.VK_CONTROL) && !Native.IsKeyDown(Native.VK_MENU);

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
