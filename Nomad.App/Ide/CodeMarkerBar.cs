using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Ide;

/// <summary>
/// Beside the editor's scroll bar, as Visual Studio's overview: a tick for each line with a find match (the match at
/// hand in the accent color), placed by its line's share of the file. Only drawn; clicks go through to the scroll bar.
/// </summary>
public sealed class CodeMarkerBar : Control
{
    public static readonly StyledProperty<CodeDocument?> DocumentProperty =
        AvaloniaProperty.Register<CodeMarkerBar, CodeDocument?>(nameof(Document));

    private CodeDocument? _doc;

    static CodeMarkerBar()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<CodeMarkerBar>(false);
    }

    public CodeDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            if (_doc is not null)
            {
                _doc.MarksChanged -= InvalidateVisual;
                _doc.Changed -= InvalidateVisual;
            }

            _doc = Document;
            if (_doc is not null)
            {
                _doc.MarksChanged += InvalidateVisual;
                _doc.Changed += InvalidateVisual;
            }

            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_doc is not { Marks.Count: > 0 } doc)
        {
            return;
        }

        var brush = this.TryFindResource("CodeFindMarkBrush", ActualThemeVariant, out var b) && b is IBrush mark ? mark : Brushes.Orange;
        var accent = this.TryFindResource("AccentBrush", ActualThemeVariant, out var a) && a is IBrush acc ? acc : Brushes.White;
        var lines = Math.Max(1, doc.Lines.Count);
        var height = Bounds.Height;
        var last = -1.0;
        foreach (var (from, _) in doc.Marks)
        {
            var y = Math.Round(from.Line / (double)lines * (height - 3));
            if (y > last)
            {
                context.FillRectangle(brush, new Rect(1, y, Bounds.Width - 2, 2));
                last = y;
            }
        }

        if (doc.CurrentMark >= 0 && doc.CurrentMark < doc.Marks.Count)
        {
            var y = Math.Round(doc.Marks[doc.CurrentMark].From.Line / (double)lines * (height - 3));
            context.FillRectangle(accent, new Rect(0, y, Bounds.Width, 3));
        }
    }
}
