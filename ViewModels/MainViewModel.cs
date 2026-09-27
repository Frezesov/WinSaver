using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinSaver.Core;
using WinSaver.Editor;

namespace WinSaver.ViewModels;

public enum UpdateState { Unknown, Checking, UpToDate, Available, Failed }

public sealed record KeepOption(int Days, string Label);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string ProjectPage = "https://github.com/Frezesov/WinSaver";
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(20);

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly KeyboardHook _keyboard;
    private readonly HotkeyService _hotkeys;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _cleanupTimer;
    private bool _autostart;
    private UpdateState _updateState;
    private ReleaseInfo? _latest;
    private Screenshot? _last;

    public event Action? CaptureRequested;
    internal event Action<Screenshot>? EditRequested;
    public event Action? OpenSettingsRequested;
    public event Action? HideRequested;
    public event Action? ExitRequested;

    internal MainViewModel(AppSettings settings, SettingsStore store, Dispatcher dispatcher)
    {
        _settings = settings;
        _store = store;
        settings.KeepDays = KeepOptions.Any(o => o.Days == settings.KeepDays) ? settings.KeepDays : 0;
        _autostart = AutostartService.IsEnabled;

        Library = new ScreenshotLibrary { Folder = ResolveFolder(settings.SaveFolder) };

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _store.Save(_settings);
        };

        _keyboard = new KeyboardHook(dispatcher);
        _keyboard.Pressed += () => CaptureRequested?.Invoke();
        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += () => CaptureRequested?.Invoke();

        NewScreenshotCommand = new RelayCommand(() => CaptureRequested?.Invoke());
        EditLastCommand = new RelayCommand(EditLast);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ResetFolderCommand = new RelayCommand(() => SetFolder(null));
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());
        OpenProjectPageCommand = new RelayCommand(() => OpenLink(ProjectPage));
        HideToTrayCommand = new RelayCommand(() => HideRequested?.Invoke());
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckUpdatesAsync(automatic: false));
        DownloadUpdateCommand = new RelayCommand(() => OpenLink(_latest?.Url ?? UpdateChecker.ReleasesPage));

        // The first automatic check waits until startup is over, later ones look once an hour whether one is due.
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(1);
            if (_settings.CheckForUpdates && IsUpdateCheckDue)
                _ = CheckUpdatesAsync(automatic: true);
        };
        _updateTimer.Start();
        RestoreUpdateState();

        _cleanupTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _cleanupTimer.Tick += (_, _) =>
        {
            _cleanupTimer.Interval = TimeSpan.FromHours(1);
            RunCleanup();
        };
        _cleanupTimer.Start();

        ApplyHooks();
        RefreshLast();
    }

    internal ScreenshotLibrary Library { get; }

    /// <summary>Tells the cleanup which files are open in the editor, so it leaves them alone.</summary>
    internal Func<string, bool> IsOpenInEditor { get; set; } = _ => false;

    public RelayCommand NewScreenshotCommand { get; }
    public RelayCommand EditLastCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ResetFolderCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenProjectPageCommand { get; }
    public RelayCommand HideToTrayCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand DownloadUpdateCommand { get; }

    // ---- Calling WinSaver

    public bool ReplaceSnipping
    {
        get => _settings.ReplaceSnippingHotkey;
        set
        {
            if (Update(_settings.ReplaceSnippingHotkey, value, v => _settings.ReplaceSnippingHotkey = v))
                ApplyHooks();
        }
    }

    public bool InterceptPrintScreen
    {
        get => _settings.InterceptPrintScreen;
        set
        {
            if (Update(_settings.InterceptPrintScreen, value, v => _settings.InterceptPrintScreen = v))
                ApplyHooks();
        }
    }

    public Hotkey Hotkey
    {
        get => new(_settings.HotkeyModifiers, _settings.HotkeyKey);
        set
        {
            if (Hotkey == value)
                return;
            _settings.HotkeyModifiers = value.Modifiers;
            _settings.HotkeyKey = value.Key;
            OnPropertyChanged();
            ScheduleSave();
            ApplyHooks();
        }
    }

    public bool IsCapturingHotkey
    {
        get;
        set
        {
            if (Set(ref field, value))
                ApplyHooks();
        }
    }

    public string? HotkeyError
    {
        get;
        private set => Set(ref field, value);
    }

    /// <summary>The shortcut shown next to "Новый скриншот", or empty when there is none.</summary>
    public string CaptureShortcutText =>
        ReplaceSnipping ? "Win + Shift + S"
        : Hotkey.IsValid ? Hotkey.ToString()
        : InterceptPrintScreen ? "PrtSc"
        : "";

    // ---- Saving

    public string SaveFolder => Library.Folder;

    public bool IsDefaultFolder => string.IsNullOrWhiteSpace(_settings.SaveFolder);

    internal void SetFolder(string? folder)
    {
        var value = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
        if (value is not null && string.Equals(value, ScreenshotLibrary.DefaultFolder, StringComparison.OrdinalIgnoreCase))
            value = null;
        if (_settings.SaveFolder == value)
            return;
        _settings.SaveFolder = value;
        Library.Folder = ResolveFolder(value);
        OnPropertyChanged(nameof(SaveFolder));
        OnPropertyChanged(nameof(IsDefaultFolder));
        ScheduleSave();
        RefreshLast();
        RunCleanup();
    }

    public IReadOnlyList<KeepOption> KeepOptions { get; } =
    [
        new(0, "Никогда"),
        new(1, "Через день"),
        new(3, "Через 3 дня"),
        new(7, "Через неделю"),
        new(14, "Через 2 недели"),
        new(30, "Через месяц"),
    ];

    public int KeepDays
    {
        get => _settings.KeepDays;
        set
        {
            if (Update(_settings.KeepDays, value, v => _settings.KeepDays = v))
                RunCleanup();
        }
    }

    // ---- Screenshots

    public bool HasLastShot => _last is not null;

    public string LastShotText => _last is null ? "Скриншотов пока нет" : When.Format(_last.Taken);

    public string LastShotDetails => _last is null
        ? $"Нажмите {(CaptureShortcutText.Length > 0 ? CaptureShortcutText : "«Новый скриншот»")}, выберите область — картинка сразу окажется в буфере обмена"
        : $"{When.Capitalize(When.Format(_last.Taken))} · {Path.GetFileName(_last.Path)}";

    internal CaptureMode CaptureMode
    {
        get => _settings.CaptureMode;
        set => Update(_settings.CaptureMode, value, v => _settings.CaptureMode = v);
    }

    internal EditorTool EditorTool
    {
        get => _settings.EditorTool;
        set => Update(_settings.EditorTool, value, v => _settings.EditorTool = v);
    }

    internal string EditorColor
    {
        get => _settings.EditorColor;
        set => Update(_settings.EditorColor, value, v => _settings.EditorColor = v);
    }

    internal double EditorSize
    {
        get => _settings.EditorSize;
        set => Update(_settings.EditorSize, value, v => _settings.EditorSize = v);
    }

    /// <summary>
    /// Puts a fresh capture on the clipboard and into the folder. The PNG is encoded once, off the UI thread,
    /// and used for both. When the chosen folder cannot be written, the default one is tried.
    /// </summary>
    internal async Task StoreAsync(BitmapSource image)
    {
        var taken = DateTime.Now;
        try
        {
            var png = await Task.Run(() => ScreenshotLibrary.EncodePng(image));
            ClipboardImage.Set(image, png);
            Screenshot saved;
            try
            {
                saved = await Library.SaveAsync(png, taken);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && !IsDefaultFolder)
            {
                ErrorLog.Write(ex);
                saved = await new ScreenshotLibrary { Folder = ScreenshotLibrary.DefaultFolder }.SaveAsync(png, taken);
            }
            SetLast(saved);
        }
        catch (Exception ex)
        {
            // Nothing awaits this task, so whatever went wrong must end up in the log rather than vanish.
            ErrorLog.Write(ex);
        }
    }

    internal void RefreshLast() => SetLast(Library.GetLast());

    internal void NotifySaved(Screenshot shot)
    {
        if (_last is null || shot.Taken >= _last.Taken)
            SetLast(shot);
    }

    private void SetLast(Screenshot? shot)
    {
        if (_last == shot)
            return;
        _last = shot;
        OnPropertyChanged(nameof(HasLastShot));
        OnPropertyChanged(nameof(LastShotText));
        OnPropertyChanged(nameof(LastShotDetails));
    }

    internal void EditLast()
    {
        RefreshLast();
        if (_last is not null)
            EditRequested?.Invoke(_last);
    }

    internal void Edit(Screenshot shot) => EditRequested?.Invoke(shot);

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Library.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Library.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ErrorLog.Write(ex);
        }
    }

    private void RunCleanup()
    {
        int days = KeepDays;
        if (days <= 0)
            return;
        var isOpen = IsOpenInEditor;
        Task.Run(() => Library.Cleanup(days, isOpen)).ContinueWith(t =>
        {
            if (t.Result > 0)
                RefreshLast();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string ResolveFolder(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? ScreenshotLibrary.DefaultFolder : folder;

    // ---- System

    public bool Autostart
    {
        get => _autostart;
        set
        {
            if (_autostart == value)
                return;
            try
            {
                AutostartService.Set(value);
                _autostart = value;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                ErrorLog.Write(ex);
                _autostart = AutostartService.IsEnabled;
            }
            OnPropertyChanged();
        }
    }

    public string VersionText { get; } = $"Версия {UpdateChecker.Current.ToString(3)}";

    public bool CheckForUpdates
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (Update(_settings.CheckForUpdates, value, v => _settings.CheckForUpdates = v) && value && IsUpdateCheckDue)
                _ = CheckUpdatesAsync(automatic: true);
        }
    }

    public bool UpdateAvailable => _updateState == UpdateState.Available && _latest is not null;

    public bool CanCheckUpdates => _updateState != UpdateState.Checking;

    public string UpdateTitle => _updateState switch
    {
        UpdateState.Checking => "Проверяю обновления…",
        UpdateState.Available when _latest is not null => $"Доступна версия {_latest.Version.ToString(3)}",
        UpdateState.UpToDate => "Установлена последняя версия",
        UpdateState.Failed => "Не удалось проверить обновления",
        _ => "Обновления",
    };

    public string UpdateDetail => _updateState switch
    {
        UpdateState.Available => "Откроется страница выпуска на GitHub: скачайте новый exe и замените им старый",
        UpdateState.Failed => "Нет связи с GitHub. Проверьте подключение к интернету и попробуйте ещё раз",
        _ => _settings.LastUpdateCheck is { } last ? $"Последняя проверка: {FormatWhen(last)}" : "Ещё не проверялось",
    };

    public string DownloadUpdateText => _latest is null ? "Скачать" : $"Скачать {_latest.Version.ToString(3)}";

    public string UpdateMenuText => _latest is null ? "Доступно обновление…" : $"Доступно обновление {_latest.Version.ToString(3)}…";

    private bool IsUpdateCheckDue =>
        _settings.LastUpdateCheck is not { } last || DateTimeOffset.Now - last > UpdateCheckInterval;

    // What the last check found survives restarts, so a found update is shown right away.
    private void RestoreUpdateState()
    {
        if (!UpdateChecker.TryParseVersion(_settings.LatestVersion, out var known) || _settings.LastUpdateCheck is null)
            return;
        if (known > UpdateChecker.Current)
        {
            _latest = new ReleaseInfo(known, UpdateChecker.ReleasePage(known));
            _updateState = UpdateState.Available;
        }
        else
        {
            _updateState = UpdateState.UpToDate;
        }
    }

    private async Task CheckUpdatesAsync(bool automatic)
    {
        if (_updateState == UpdateState.Checking)
            return;
        var previous = _updateState;
        SetUpdateState(UpdateState.Checking);
        try
        {
            var latest = await UpdateChecker.GetLatestAsync(CancellationToken.None);
            _latest = latest;
            _settings.LastUpdateCheck = DateTimeOffset.Now;
            _settings.LatestVersion = latest.Version.ToString(3);
            ScheduleSave();
            SetUpdateState(latest.Version > UpdateChecker.Current ? UpdateState.Available : UpdateState.UpToDate);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Being offline is normal for a background check: keep what is known and try again later.
            SetUpdateState(automatic && previous != UpdateState.Unknown ? previous : UpdateState.Failed);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            ErrorLog.Write(ex);
            SetUpdateState(automatic && previous != UpdateState.Unknown ? previous : UpdateState.Failed);
        }
    }

    private void SetUpdateState(UpdateState state)
    {
        _updateState = state;
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(CanCheckUpdates));
        OnPropertyChanged(nameof(UpdateTitle));
        OnPropertyChanged(nameof(UpdateDetail));
        OnPropertyChanged(nameof(DownloadUpdateText));
        OnPropertyChanged(nameof(UpdateMenuText));
    }

    private static string FormatWhen(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today)
            return $"сегодня в {local:HH:mm}";
        if (local.Date == today.AddDays(-1))
            return $"вчера в {local:HH:mm}";
        return local.ToString("d MMMM yyyy", new System.Globalization.CultureInfo("ru-RU"));
    }

    // ---- Plumbing

    private bool Update<T>(T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return false;
        assign(value);
        OnPropertyChanged(name);
        ScheduleSave();
        return true;
    }

    private void ApplyHooks()
    {
        _keyboard.Configure(ReplaceSnipping, InterceptPrintScreen);

        if (IsCapturingHotkey || Hotkey.IsEmpty)
        {
            _hotkeys.Unregister();
            HotkeyError = null;
        }
        else if (!Hotkey.IsValid)
        {
            _hotkeys.Unregister();
            HotkeyError = "Добавьте Ctrl, Alt, Shift или Win — без них можно назначить только F1–F24";
        }
        else
        {
            HotkeyError = _hotkeys.Register(Hotkey)
                ? null
                : "Сочетание уже занято системой или другой программой — выберите другое";
        }
        OnPropertyChanged(nameof(CaptureShortcutText));
        OnPropertyChanged(nameof(LastShotDetails));
    }

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write(ex);
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Dispose()
    {
        _updateTimer.Stop();
        _cleanupTimer.Stop();
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            _store.Save(_settings);
        }
        _keyboard.Dispose();
        _hotkeys.Dispose();
    }
}
