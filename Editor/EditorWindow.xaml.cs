using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinSaver.Core;
using WinSaver.ViewModels;

namespace WinSaver.Editor;

public partial class EditorWindow : Window
{
    private static readonly string[] Palette =
        ["#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#0A84FF", "#AF52DE", "#FFFFFF", "#000000"];

    private readonly MainViewModel _vm;
    private readonly Screenshot _shot;
    private readonly EditDocument _doc;
    private readonly DispatcherTimer _statusTimer;
    private bool _closeConfirmed;
    private bool _busy;

    internal EditorWindow(MainViewModel vm, Screenshot shot, BitmapSource image)
    {
        InitializeComponent();
        _vm = vm;
        _shot = shot;
        _doc = new EditDocument(image);
        Title = $"{When.Capitalize(When.Format(shot.Taken, seconds: true))} — WinSaver";

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusText.Text = "";
        };

        Board.TextFont = (FontFamily)FindResource("AppFontFamily");
        BuildColors();
        Board.Color = ParseColor(vm.EditorColor);
        Board.Size = Math.Clamp(vm.EditorSize, SizeSlider.Minimum, SizeSlider.Maximum);
        SizeSlider.Value = Board.Size;
        SizeSlider.ValueChanged += OnSizeChanged;
        Board.Tool = vm.EditorTool == EditorTool.Crop ? EditorTool.Pen : vm.EditorTool;
        foreach (var button in ToolButtons.Children.OfType<RadioButton>())
            button.Checked += OnToolChecked;
        SyncTool();
        SyncColor();
        UpdateSizePreview();

        Board.Load(_doc);
        _doc.Changed += UpdateCommands;
        Board.ViewChanged += () => ZoomText.Text = $"{Math.Round(Board.ZoomPercent)} %";
        Board.CropModeChanged += OnCropModeChanged;
        Board.ToolChanged += () =>
        {
            SyncTool();
            if (Board.Tool != EditorTool.Crop)
                _vm.EditorTool = Board.Tool;
        };

        FitToImage(image);
        UpdateCommands();
    }

    /// <summary>Reads a screenshot file without keeping it locked, as plain opaque 32-bit pixels.</summary>
    internal static BitmapSource? LoadImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Bgr32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
            int stride = frame.PixelWidth * 4;
            var pixels = new byte[stride * frame.PixelHeight];
            frame.CopyPixels(pixels, stride, 0);
            var image = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            ErrorLog.Write(ex);
            return null;
        }
    }

    // The window opens large enough to show the screenshot at 100 %, within 90 % of the screen.
    private void FitToImage(BitmapSource image)
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var work = SystemParameters.WorkArea;
        double width = image.PixelWidth / dpi + 64;
        double height = image.PixelHeight / dpi + 64 + 56 + 72 + 32;
        Width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, work.Width * 0.9));
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, work.Height * 0.9));
    }

    private void BuildColors()
    {
        foreach (var hex in Palette)
        {
            var color = ParseColor(hex);
            var swatch = new RadioButton
            {
                GroupName = "Color",
                Background = new SolidColorBrush(color),
                Tag = hex,
                ToolTip = ColorName(hex),
            };
            swatch.SetResourceReference(StyleProperty, "ColorSwatch");
            System.Windows.Automation.AutomationProperties.SetName(swatch, ColorName(hex));
            swatch.Checked += (_, _) =>
            {
                Board.Color = color;
                _vm.EditorColor = hex;
                UpdateSizePreview();
            };
            ColorButtons.Children.Add(swatch);
        }
    }

    private static string ColorName(string hex) => hex switch
    {
        "#FF3B30" => "Красный",
        "#FF9500" => "Оранжевый",
        "#FFCC00" => "Жёлтый",
        "#34C759" => "Зелёный",
        "#0A84FF" => "Синий",
        "#AF52DE" => "Фиолетовый",
        "#FFFFFF" => "Белый",
        _ => "Чёрный",
    };

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return Colors.Red;
        }
    }

    private void SyncTool()
    {
        foreach (var button in ToolButtons.Children.OfType<RadioButton>())
            button.IsChecked = (EditorTool)button.Tag == Board.Tool;
    }

    private void SyncColor()
    {
        foreach (var swatch in ColorButtons.Children.OfType<RadioButton>())
            swatch.IsChecked = string.Equals((string)swatch.Tag, _vm.EditorColor, StringComparison.OrdinalIgnoreCase);
        if (!ColorButtons.Children.OfType<RadioButton>().Any(s => s.IsChecked == true))
            ((RadioButton)ColorButtons.Children[0]).IsChecked = true;
    }

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        var tool = (EditorTool)((RadioButton)sender).Tag;
        Board.Tool = tool;
        if (tool != EditorTool.Crop)
            _vm.EditorTool = tool;
        Board.Focus();
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Board.Size = e.NewValue;
        _vm.EditorSize = e.NewValue;
        UpdateSizePreview();
    }

    private void UpdateSizePreview()
    {
        double size = Math.Clamp(Board.Size + 2, 3, 26);
        SizePreview.Width = SizePreview.Height = size;
        SizePreview.Fill = new SolidColorBrush(Board.Color);
    }

    private void OnCropModeChanged()
    {
        bool cropping = Board.IsCropping;
        CropButtons.Visibility = cropping ? Visibility.Visible : Visibility.Collapsed;
        StyleControls.Visibility = cropping ? Visibility.Collapsed : Visibility.Visible;
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        UndoButton.IsEnabled = _doc.CanUndo;
        RedoButton.IsEnabled = _doc.CanRedo;
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ---- Commands

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (Board.IsCropping)
        {
            Board.CancelCrop();
            return;
        }
        Board.CommitText();
        _doc.Undo();
    }

    private void Redo()
    {
        if (Board.IsCropping)
            return;
        Board.CommitText();
        _doc.Redo();
    }

    private void OnCropApply(object sender, RoutedEventArgs e) => Board.ApplyCrop();

    private void OnCropCancel(object sender, RoutedEventArgs e) => Board.CancelCrop();

    private void OnCropReset(object sender, RoutedEventArgs e) => Board.ResetCrop();

    private void OnCopy(object sender, RoutedEventArgs e) => _ = CopyAsync();

    private void OnSaveAs(object sender, RoutedEventArgs e) => _ = SaveAsAsync();

    private void OnDone(object sender, RoutedEventArgs e) => _ = DoneAsync();

    private BitmapSource Compose()
    {
        Board.CommitText();
        Board.ApplyCrop();
        return ImageComposer.Compose(_doc);
    }

    private async Task CopyAsync()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            var image = Compose();
            var png = await Task.Run(() => ScreenshotLibrary.EncodePng(image));
            if (ClipboardImage.Set(image, png))
                ShowStatus("Скопировано в буфер обмена");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Overwrites the screenshot file with the edited picture, copies it and closes the editor.</summary>
    private async Task DoneAsync()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            var image = Compose();
            var png = await Task.Run(() => ScreenshotLibrary.EncodePng(image));
            ClipboardImage.Set(image, png);
            if (!_doc.IsUntouched)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_shot.Path)!);
                        ScreenshotLibrary.WriteAtomic(_shot.Path, png);
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ErrorLog.Write(ex);
                    ConfirmDialog.Show(this, "Не удалось сохранить скриншот",
                        $"Файл «{Path.GetFileName(_shot.Path)}» не записался: {ex.Message} Картинка уже в буфере обмена — можно сохранить её через «Сохранить как…».",
                        "Понятно", null);
                    return;
                }
                _vm.NotifySaved(_shot);
            }
            _doc.MarkSaved();
            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveAsAsync()
    {
        if (_busy)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить скриншот как",
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg",
            FileName = Path.GetFileNameWithoutExtension(_shot.Path),
            InitialDirectory = Path.GetDirectoryName(_shot.Path),
            DefaultExt = ".png",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _busy = true;
        try
        {
            var image = Compose();
            var path = dialog.FileName;
            bool jpeg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            try
            {
                await Task.Run(() => ScreenshotLibrary.WriteAtomic(path, jpeg ? EncodeJpeg(image) : ScreenshotLibrary.EncodePng(image)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(ex);
                ConfirmDialog.Show(this, "Не удалось сохранить файл", ex.Message, "Понятно", null);
                return;
            }
            _doc.MarkSaved();
            ShowStatus($"Сохранено: {Path.GetFileName(path)}");
        }
        finally
        {
            _busy = false;
        }
    }

    private static byte[] EncodeJpeg(BitmapSource image)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ---- Keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Typing on the picture and the size slider keep their own keys.
        if (Keyboard.FocusedElement is TextBox)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods == ModifierKeys.Control, ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);

        if (key == Key.Space && mods == ModifierKeys.None)
        {
            Board.SetSpaceDown(true);
            e.Handled = true;
            return;
        }

        e.Handled = true;
        switch (key)
        {
            case Key.Escape when mods == ModifierKeys.None:
                if (Board.IsCropping)
                    Board.CancelCrop();
                else if (!Board.CancelGesture())
                    Close();
                return;
            case Key.Enter when mods == ModifierKeys.None && Board.IsCropping:
                Board.ApplyCrop();
                return;
            case Key.Z when ctrl:
                Undo();
                return;
            case Key.Y when ctrl:
            case Key.Z when ctrlShift:
                Redo();
                return;
            case Key.C when ctrl:
                _ = CopyAsync();
                return;
            case Key.S when ctrl:
                _ = DoneAsync();
                return;
            case Key.S when ctrlShift:
                _ = SaveAsAsync();
                return;
            case Key.D0 or Key.NumPad0 when ctrl:
                Board.Fit();
                return;
            case Key.OemPlus or Key.Add when ctrl:
                Board.ZoomBy(1.25);
                return;
            case Key.OemMinus or Key.Subtract when ctrl:
                Board.ZoomBy(0.8);
                return;
        }

        if (mods == ModifierKeys.None && ToolForKey(key) is { } tool)
        {
            Board.Tool = tool;
            if (tool != EditorTool.Crop)
                _vm.EditorTool = tool;
            return;
        }
        e.Handled = false;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key == Key.Space)
            Board.SetSpaceDown(false);
    }

    private static EditorTool? ToolForKey(Key key) => key switch
    {
        Key.P => EditorTool.Pen,
        Key.M => EditorTool.Marker,
        Key.A => EditorTool.Arrow,
        Key.R => EditorTool.Rectangle,
        Key.O => EditorTool.Ellipse,
        Key.T => EditorTool.Text,
        Key.B => EditorTool.Pixelate,
        Key.C => EditorTool.Crop,
        Key.E => EditorTool.Eraser,
        _ => null,
    };

    // ---- Closing

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed)
            return;
        Board.CommitText();
        if (!_doc.IsDirty)
            return;

        e.Cancel = true;
        Dispatcher.BeginInvoke(() =>
        {
            var answer = ConfirmDialog.Show(this, "Сохранить изменения?",
                "Если закрыть редактор без сохранения, всё нарисованное пропадёт.", "Сохранить", "Не сохранять");
            if (answer == ConfirmDialog.Result.Primary)
            {
                _ = DoneAsync();
            }
            else if (answer == ConfirmDialog.Result.Secondary)
            {
                _closeConfirmed = true;
                Close();
            }
        });
    }
}
