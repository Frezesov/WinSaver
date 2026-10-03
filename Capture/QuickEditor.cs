using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSaver.Editor;
using WinSaver.ViewModels;

namespace WinSaver.Capture;

/// <summary>
/// Drawing on the picked area before the screenshot is kept, like quick markup in Snipping Tool. Every monitor
/// the area touches gets an editor canvas over its part of the area, all showing one document, and a toolbar
/// sits next to the area. Tool, colour and thickness are the ones of the editor window.
/// </summary>
internal sealed class QuickEditor
{
    private readonly MainViewModel _vm;
    private readonly EditDocument _doc;
    private readonly List<EditorCanvas> _boards = [];
    private readonly QuickEditToolbar _toolbar;
    private bool _completed;

    public QuickEditor(MainViewModel vm, BitmapSource image, PixelRect area, IEnumerable<OverlayWindow> windows, OverlayWindow toolbarHost)
    {
        _vm = vm;
        _doc = new EditDocument(image);
        Tool = vm.EditorTool == EditorTool.Crop ? EditorTool.Pen : vm.EditorTool;
        ColorHex = EditorTools.Palette.FirstOrDefault(hex => string.Equals(hex, vm.EditorColor, StringComparison.OrdinalIgnoreCase))
                   ?? EditorTools.Palette[0];
        var color = EditorTools.ParseColor(ColorHex);
        double size = Math.Clamp(vm.EditorSize, EditorTools.MinSize, EditorTools.MaxSize);
        var font = (FontFamily)Application.Current.FindResource("AppFontFamily");

        foreach (var window in windows)
        {
            var part = area.Intersect(window.Monitor.Bounds);
            if (part.IsEmpty)
                continue;
            var board = new EditorCanvas { TextFont = font, Tool = Tool, Color = color, Size = size };
            board.Load(_doc);
            window.ShowBoard(board, part);
            board.PinView(new Point(part.X - area.X, part.Y - area.Y));
            _boards.Add(board);
        }

        _toolbar = new QuickEditToolbar(this);
        toolbarHost.ShowQuickBar(_toolbar, area);
        _doc.Changed += () => _toolbar.SyncHistory(_doc.CanUndo, _doc.CanRedo);
    }

    /// <summary>The picture with what is drawn on it, or null when the screenshot is cancelled.</summary>
    public event Action<BitmapSource?>? Completed;

    public EditorTool Tool { get; private set; }

    /// <summary>The chosen colour as "#RRGGBB", one of <see cref="EditorTools.Palette"/>.</summary>
    public string ColorHex { get; private set; }

    public void SetTool(EditorTool tool)
    {
        if (tool == Tool || tool == EditorTool.Crop)
            return;
        Tool = tool;
        foreach (var board in _boards)
            board.Tool = tool;
        _vm.EditorTool = tool;
        _toolbar.SyncTool(tool);
    }

    public void SetColor(string hex)
    {
        ColorHex = hex;
        var color = EditorTools.ParseColor(hex);
        foreach (var board in _boards)
            board.Color = color;
        _vm.EditorColor = hex;
    }

    public void Undo()
    {
        CommitText();
        _doc.Undo();
    }

    public void Redo()
    {
        CommitText();
        _doc.Redo();
    }

    public void Save()
    {
        if (_completed)
            return;
        _completed = true;
        CommitText();
        Completed?.Invoke(ImageComposer.Compose(_doc));
    }

    public void Cancel()
    {
        if (_completed)
            return;
        _completed = true;
        Completed?.Invoke(null);
    }

    public void PreviewKeyDown(KeyEventArgs e)
    {
        // Typing on the picture keeps its own keys.
        if (Keyboard.FocusedElement is TextBox)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods == ModifierKeys.Control, ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);

        e.Handled = true;
        switch (key)
        {
            case Key.Escape when mods == ModifierKeys.None:
                if (!_boards.Any(board => board.CancelGesture()))
                    Cancel();
                return;
            case Key.Enter when mods == ModifierKeys.None:
            case Key.S or Key.C when ctrl:
                Save();
                return;
            case Key.Z when ctrl:
                Undo();
                return;
            case Key.Y when ctrl:
            case Key.Z when ctrlShift:
                Redo();
                return;
        }

        if (mods == ModifierKeys.None && EditorTools.ForKey(key) is { } tool && tool != EditorTool.Crop)
        {
            SetTool(tool);
            return;
        }
        e.Handled = false;
    }

    private void CommitText()
    {
        foreach (var board in _boards)
            board.CommitText();
    }
}
