using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinSaver.Controls;
using WinSaver.Core;
using WinSaver.ViewModels;
using Forms = System.Windows.Forms;

namespace WinSaver.Tray;

internal sealed class TrayIcon : IDisposable
{
    private const string AppName = "WinSaver";
    private const int RecentCount = 10;
    // Long enough for the menu to fade out, so it is not in the screenshot.
    private static readonly TimeSpan CaptureDelay = TimeSpan.FromMilliseconds(250);

    private readonly MainViewModel _vm;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly System.Drawing.Icon _icon;
    private readonly ContextMenu _menu;
    private readonly MenuItem _captureItem;
    private readonly MenuItem _editItem;
    private readonly MenuItem _recentItem;
    private readonly ThumbnailCache _thumbnails = new();
    private Window? _menuHost;
    private bool _captureAfterClose;

    public TrayIcon(MainViewModel vm)
    {
        _vm = vm;
        _icon = LoadIcon("app.ico", Forms.SystemInformation.SmallIconSize);

        _notifyIcon = new Forms.NotifyIcon { Icon = _icon, Visible = false };
        _notifyIcon.MouseUp += OnMouseUp;

        _captureItem = new MenuItem { Header = "Новый скриншот" };
        _captureItem.Click += (_, _) => _captureAfterClose = true;
        _editItem = new MenuItem { FontWeight = FontWeights.SemiBold };
        _editItem.Click += (_, _) => _vm.EditLast();
        _recentItem = new MenuItem { Header = new TextBlock { Text = "Недавние" } };

        _menu = new ContextMenu { DataContext = vm };
        _menu.SetResourceReference(FrameworkElement.StyleProperty, "MenuStyle");
        _menu.Items.Add(_captureItem);
        _menu.Items.Add(_editItem);
        _menu.Items.Add(_recentItem);
        _menu.Items.Add(new MenuItem { Header = "Открыть папку со скриншотами", Command = vm.OpenFolderCommand });
        _menu.Items.Add(new Separator());
        _menu.Items.Add(CheckItem("Заменять Win + Shift + S", nameof(MainViewModel.ReplaceSnipping)));
        _menu.Items.Add(CheckItem("Запускать вместе с Windows", nameof(MainViewModel.Autostart)));
        var updateItem = new MenuItem { Command = vm.DownloadUpdateCommand };
        updateItem.SetBinding(HeaderedItemsControl.HeaderProperty, nameof(MainViewModel.UpdateMenuText));
        updateItem.SetBinding(UIElement.VisibilityProperty,
            new Binding(nameof(MainViewModel.UpdateAvailable)) { Converter = new BooleanToVisibilityConverter() });
        _menu.Items.Add(updateItem);
        _menu.Items.Add(new MenuItem { Header = "Настройки…", Command = vm.OpenSettingsCommand });
        _menu.Items.Add(new Separator());
        _menu.Items.Add(new MenuItem { Header = "Выход", Command = vm.ExitCommand });
        _menu.Closed += OnMenuClosed;
        _menu.Opened += (_, _) => AlignRecentHeader();

        _vm.PropertyChanged += OnViewModelChanged;
        RefreshTip();
        _notifyIcon.Visible = true;
    }

    private static MenuItem CheckItem(string header, string path)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, StaysOpenOnClick = false };
        item.SetBinding(MenuItem.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay });
        return item;
    }

    private static System.Drawing.Icon LoadIcon(string name, System.Drawing.Size size)
    {
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))
                   ?? throw new InvalidOperationException($"Missing resource {name}");
        using var stream = info.Stream;
        return new System.Drawing.Icon(stream, size);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.LastShotText))
            RefreshTip();
    }

    private void RefreshTip()
    {
        var tip = _vm.HasLastShot ? $"{AppName}{Environment.NewLine}Последний скриншот: {_vm.LastShotText}" : AppName;
        _notifyIcon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    // A left click is the quick way to the editor; with nothing to edit it shows the menu instead.
    private void OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            _vm.RefreshLast();
            if (_vm.HasLastShot)
                _vm.EditLast();
            else
                ShowMenu();
        }
        else if (e.Button == Forms.MouseButtons.Right)
        {
            ShowMenu();
        }
    }

    // A context menu only closes on outside clicks when its app owns the foreground,
    // so an invisible host window is activated first.
    private void ShowMenu()
    {
        var recent = _vm.Library.GetRecent(RecentCount);
        _vm.RefreshLast();

        _captureItem.InputGestureText = _vm.CaptureShortcutText;
        if (recent.Count > 0)
        {
            _editItem.Header = "Редактировать последний";
            _editItem.InputGestureText = When.Format(recent[0].Taken);
            _editItem.IsEnabled = true;
        }
        else
        {
            _editItem.Header = "Скриншотов пока нет";
            _editItem.InputGestureText = "";
            _editItem.IsEnabled = false;
        }

        _recentItem.Items.Clear();
        foreach (var shot in recent)
            _recentItem.Items.Add(RecentItem(shot));
        _recentItem.IsEnabled = recent.Count > 0;

        _menuHost ??= CreateMenuHost();
        _menuHost.Show();
        _menuHost.Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(_menuHost).Handle);

        MenuPlacement.OpenAtCursor(_menu, _menuHost);
    }

    // The Fluent template of an item with a submenu has no check column, so its text starts further left than
    // the others. The gap is measured on the open menu rather than hard-coded, so it follows the theme.
    private void AlignRecentHeader()
    {
        if (_recentItem.Header is not TextBlock header || header.Margin.Left != 0)
            return;
        _menu.UpdateLayout();
        var reference = FindHeaderPresenter(_captureItem);
        var target = FindHeaderPresenter(_recentItem);
        if (reference is null || target is null)
            return;
        double gap = reference.TranslatePoint(new Point(), _menu).X - target.TranslatePoint(new Point(), _menu).X;
        if (gap > 0.5)
            header.Margin = new Thickness(gap, 0, 0, 0);
    }

    private static ContentPresenter? FindHeaderPresenter(MenuItem item)
    {
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(item);
        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            if (node is ContentPresenter presenter && ReferenceEquals(presenter.Content, item.Header))
                return presenter;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                pending.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }

    private MenuItem RecentItem(Screenshot shot)
    {
        var thumbnail = new Image { Width = 64, Height = 40, Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(thumbnail, BitmapScalingMode.HighQuality);
        var frame = new Border
        {
            Width = 64,
            Height = 40,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = thumbnail,
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        _thumbnails.Load(shot.Path, thumbnail);

        var text = new TextBlock
        {
            Text = When.Capitalize(When.Format(shot.Taken, seconds: true)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        header.Children.Add(frame);
        header.Children.Add(text);

        var item = new MenuItem { Header = header };
        System.Windows.Automation.AutomationProperties.SetName(item, "Скриншот, " + text.Text);
        item.Click += (_, _) => _vm.Edit(shot);
        return item;
    }

    private void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        _menuHost?.Hide();
        if (!_captureAfterClose)
            return;
        _captureAfterClose = false;
        var timer = new DispatcherTimer { Interval = CaptureDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _vm.NewScreenshotCommand.Execute(null);
        };
        timer.Start();
    }

    private static Window CreateMenuHost() => new()
    {
        Width = 1,
        Height = 1,
        Left = -32000,
        Top = -32000,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        ShowActivated = true,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        Topmost = true,
        Title = "",
    };

    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
        _menuHost?.Close();
    }

    /// <summary>
    /// Small previews for the recent list, decoded off the UI thread so the menu opens at once.
    /// A preview is reused until its file changes, e.g. after editing.
    /// </summary>
    private sealed class ThumbnailCache
    {
        private const int Capacity = 32;
        private readonly Dictionary<string, (DateTime Stamp, ImageSource Image)> _cache = new(StringComparer.OrdinalIgnoreCase);

        public void Load(string path, Image target)
        {
            DateTime stamp;
            try
            {
                stamp = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            if (_cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                target.Source = cached.Image;
                return;
            }

            Task.Run(() => Decode(path)).ContinueWith(t =>
            {
                if (t.Result is not { } image)
                    return;
                if (_cache.Count >= Capacity)
                    _cache.Clear();
                _cache[path] = (stamp, image);
                target.Source = image;
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static ImageSource? Decode(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.DecodePixelHeight = 96;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
            {
                return null;
            }
        }
    }
}
