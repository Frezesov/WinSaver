using System.ComponentModel;
using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WinSaver.Capture;
using WinSaver.Core;
using WinSaver.Editor;
using WinSaver.Themes;
using WinSaver.Tray;
using WinSaver.ViewModels;

namespace WinSaver;

public partial class App : Application
{
    private readonly Dictionary<string, EditorWindow> _editors = new(StringComparer.OrdinalIgnoreCase);
    private SingleInstance? _instance;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private CaptureSession? _session;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool startInTray = e.Args.Any(a => string.Equals(a, AutostartService.TrayArgument, StringComparison.OrdinalIgnoreCase));

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            if (!startInTray)
            {
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                _instance.SignalFirstInstance();
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        ThemeAccent.Attach(this);

        // Respect "Animation effects" off in Windows settings; template content resolves these keys lazily.
        if (!SystemParameters.ClientAreaAnimation)
        {
            Resources["MotionFast"] = new Duration(TimeSpan.Zero);
            Resources["MotionMenu"] = new Duration(TimeSpan.Zero);
            Resources["MotionExpand"] = new Duration(TimeSpan.Zero);
        }

        // Menus fade in from their own template; the popup's system slide/scroll would run on top of it.
        Resources[SystemParameters.MenuPopupAnimationKey] = PopupAnimation.None;

        var store = new SettingsStore();
        _vm = new MainViewModel(store.Load(), store, Dispatcher);
        _vm.CaptureRequested += StartCapture;
        _vm.EditRequested += OpenEditor;
        _vm.OpenSettingsRequested += ShowSettings;
        _vm.HideRequested += () => _settingsWindow?.Hide();
        _vm.ExitRequested += ExitApp;
        _vm.IsOpenInEditor = path => _editors.ContainsKey(path);

        _tray = new TrayIcon(_vm);

        try
        {
            AutostartService.RefreshPath();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorLog.Write(ex);
        }

        _instance.ListenForSignals(() => Dispatcher.BeginInvoke(ShowSettings));

        if (!startInTray)
            ShowSettings();
    }

    private void StartCapture()
    {
        if (_session is not null || _vm is null)
            return;
        var session = new CaptureSession(_vm.CaptureMode);
        _session = session;
        session.ModeChanged += mode => _vm.CaptureMode = mode;
        session.Completed += async image =>
        {
            _session = null;
            if (image is not null)
                await _vm.StoreAsync(image);
            ReleaseMemory();
        };
        try
        {
            session.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException or ArgumentException)
        {
            ErrorLog.Write(ex);
            session.Cancel();
            _session = null;
        }
    }

    private void OpenEditor(Screenshot shot)
    {
        if (_vm is null)
            return;
        if (_editors.TryGetValue(shot.Path, out var open))
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        if (!File.Exists(shot.Path) || EditorWindow.LoadImage(shot.Path) is not { } image)
        {
            _vm.RefreshLast();
            return;
        }

        var editor = new EditorWindow(_vm, shot, image);
        _editors[shot.Path] = editor;
        editor.Closed += (_, _) =>
        {
            _editors.Remove(shot.Path);
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ReleaseMemory);
        };
        editor.Show();
        editor.Activate();
    }

    // A capture holds the whole desktop as pixels, tens of megabytes on large screens, partly in native buffers
    // that only finalizers free. A tray app idles for hours, so it hands that memory back as soon as it is done.
    private static void ReleaseMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private void ShowSettings()
    {
        if (_vm is null)
            return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_vm);
            _settingsWindow.Closing += OnSettingsClosing;
        }
        if (!_settingsWindow.IsVisible)
            _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void OnSettingsClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
            return;
        e.Cancel = true;
        _settingsWindow?.Hide();
    }

    private void ExitApp()
    {
        _exiting = true;
        _session?.Cancel();
        foreach (var editor in _editors.Values.ToList())
            editor.Close();
        // An editor with unsaved changes asks first; the app stays open, and the user exits again once it is answered.
        if (_editors.Count > 0)
        {
            _exiting = false;
            return;
        }
        _settingsWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _vm?.Dispose();
        ThemeAccent.Detach();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception);
        e.Handled = true;
    }
}
