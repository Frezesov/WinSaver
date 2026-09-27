using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSaver.Editor;

/// <summary>The screenshot being edited: the untouched image, what is drawn over it, the crop, and undo/redo.</summary>
internal sealed class EditDocument
{
    private readonly List<Annotation> _annotations = [];
    private readonly Stack<IEdit> _undo = new();
    private readonly Stack<IEdit> _redo = new();
    private int _savedDepth;

    public EditDocument(BitmapSource image)
    {
        Image = image;
        Crop = FullRect;
    }

    public BitmapSource Image { get; }

    public IReadOnlyList<Annotation> Annotations => _annotations;

    public Int32Rect Crop { get; private set; }

    public Int32Rect FullRect => new(0, 0, Image.PixelWidth, Image.PixelHeight);

    public bool IsCropped => !Crop.Equals(FullRect);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool IsDirty => _undo.Count != _savedDepth;

    public bool IsUntouched => _annotations.Count == 0 && !IsCropped;

    public event Action? Changed;

    public void Add(Annotation annotation) => Do(new AddEdit(annotation));

    public void Remove(Annotation annotation)
    {
        int index = _annotations.IndexOf(annotation);
        if (index >= 0)
            Do(new RemoveEdit(annotation, index));
    }

    public void Replace(Annotation old, Annotation replacement)
    {
        int index = _annotations.IndexOf(old);
        if (index >= 0)
            Do(new ReplaceEdit(old, replacement, index));
    }

    public void SetCrop(Int32Rect crop)
    {
        if (!crop.Equals(Crop))
            Do(new CropEdit(Crop, crop));
    }

    public Annotation? HitTest(Point point, double tolerance)
    {
        for (int i = _annotations.Count - 1; i >= 0; i--)
            if (_annotations[i].HitTest(point, tolerance))
                return _annotations[i];
        return null;
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;
        var edit = _undo.Pop();
        edit.Revert(this);
        _redo.Push(edit);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;
        var edit = _redo.Pop();
        edit.Apply(this);
        _undo.Push(edit);
        Changed?.Invoke();
    }

    public void MarkSaved()
    {
        _savedDepth = _undo.Count;
        Changed?.Invoke();
    }

    /// <param name="except">An annotation left out while it is being edited in place.</param>
    public void Render(DrawingContext dc, Annotation? except = null)
    {
        dc.DrawImage(Image, new Rect(0, 0, Image.PixelWidth, Image.PixelHeight));
        foreach (var annotation in _annotations)
            if (annotation != except)
                annotation.Render(dc);
    }

    private void Do(IEdit edit)
    {
        // Once a state before the saved one is edited, the saved state can no longer be reached by undo/redo.
        if (_savedDepth > _undo.Count)
            _savedDepth = -1;
        edit.Apply(this);
        _undo.Push(edit);
        _redo.Clear();
        Changed?.Invoke();
    }

    private interface IEdit
    {
        void Apply(EditDocument doc);
        void Revert(EditDocument doc);
    }

    private sealed record AddEdit(Annotation Annotation) : IEdit
    {
        public void Apply(EditDocument doc) => doc._annotations.Add(Annotation);
        public void Revert(EditDocument doc) => doc._annotations.Remove(Annotation);
    }

    private sealed record RemoveEdit(Annotation Annotation, int Index) : IEdit
    {
        public void Apply(EditDocument doc) => doc._annotations.RemoveAt(Index);
        public void Revert(EditDocument doc) => doc._annotations.Insert(Index, Annotation);
    }

    private sealed record ReplaceEdit(Annotation Old, Annotation New, int Index) : IEdit
    {
        public void Apply(EditDocument doc) => doc._annotations[Index] = New;
        public void Revert(EditDocument doc) => doc._annotations[Index] = Old;
    }

    private sealed record CropEdit(Int32Rect Old, Int32Rect New) : IEdit
    {
        public void Apply(EditDocument doc) => doc.Crop = New;
        public void Revert(EditDocument doc) => doc.Crop = Old;
    }
}
