using System.Windows.Interop;

namespace WinSaver.Core;

internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x5753;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("WinSaver.Hotkeys")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    public bool Register(Hotkey hotkey)
    {
        Unregister();
        if (!hotkey.IsValid)
            return false;
        _registered = Native.RegisterHotKey(_source.Handle, HotkeyId, hotkey.NativeModifiers | Native.MOD_NOREPEAT, hotkey.VirtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered)
            return;
        Native.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }
}
