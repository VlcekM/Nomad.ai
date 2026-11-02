using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Ide;

/// <summary>
/// Over the editor's scroll bar, as Visual Studio's overview, each mark placed by its line's share of the file. Left of
/// the thumb, the lines changed since the file was opened: an outline while unsaved, filled once saved (Ctrl+S). Right
/// of it, errors and warnings, and a tick for each find match (the match at hand in the accent color). Only drawn;
/// clicks go through to the scroll bar.
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

    private const double Lane = 4;
    private const double LeftLane = 1.5;

    private double RightLane => Bounds.Width - Lane - 1.5;

    public override void Render(DrawingContext context)
    {
        if (_doc is not { } doc)
        {
            return;
        }

        var lines = Math.Max(1, doc.Lines.Count);
        var height = Bounds.Height;
        double Y(int line) => Math.Round(line / (double)lines * (height - 3));

        DrawChanges(context, doc, Y);
        DrawDiagnostics(context, doc, Y);
        DrawFinds(context, doc, Y);
    }

    // Runs of changed lines: unsaved ones outlined, saved ones filled.
    private void DrawChanges(DrawingContext context, CodeDocument doc, Func<int, double> y)
    {
        var green = this.TryFindResource("DiffAddBrush", ActualThemeVariant, out var g) && g is IBrush add ? add : Brushes.Green;
        var outline = new Pen(green, 1);
        var count = doc.Lines.Count;
        for (var line = 0; line < count; line++)
        {
            var unsaved = doc.IsLineChanged(line);
            if (!unsaved && !doc.IsLineSaved(line))
            {
                continue;
            }

            var end = line;
            while (end + 1 < count && doc.IsLineChanged(end + 1) == unsaved && (unsaved || doc.IsLineSaved(end + 1)))
            {
                end++;
            }

            var top = y(line);
            var rect = new Rect(LeftLane, top, Lane, Math.Max(4, y(end + 1) - top));
            if (unsaved)
            {
                context.DrawRectangle(null, outline, rect.Deflate(0.5));
            }
            else
            {
                context.FillRectangle(green, rect);
            }

            line = end;
        }
    }

    // Errors and warnings, as Visual Studio's: a tick each in their color (an error over a warning on the same spot).
    private void DrawDiagnostics(DrawingContext context, CodeDocument doc, Func<int, double> y)
    {
        if (doc.Diagnostics.Count == 0)
        {
            return;
        }

        var danger = this.TryFindResource("DangerBrush", ActualThemeVariant, out var r) && r is IBrush red ? red : Brushes.Red;
        var green = new SolidColorBrush(Color.FromRgb(0x5B, 0xA8, 0x4E));
        foreach (var severity in new[] { Nomad.Core.Lsp.LspSeverity.Warning, Nomad.Core.Lsp.LspSeverity.Error })
        {
            foreach (var (d, _) in doc.Diagnostics)
            {
                if (d.Severity == severity)
                {
                    context.FillRectangle(severity == Nomad.Core.Lsp.LspSeverity.Error ? danger : green, new Rect(RightLane, y(d.Line), Lane, 3));
                }
            }
        }
    }

    private void DrawFinds(DrawingContext context, CodeDocument doc, Func<int, double> y)
    {
        if (doc.Marks.Count == 0)
        {
            return;
        }

        var brush = this.TryFindResource("CodeFindMarkBrush", ActualThemeVariant, out var b) && b is IBrush mark ? mark : Brushes.Orange;
        var accent = this.TryFindResource("AccentBrush", ActualThemeVariant, out var a) && a is IBrush acc ? acc : Brushes.White;
        var last = -1.0;
        foreach (var (from, _) in doc.Marks)
        {
            var top = y(from.Line);
            if (top > last)
            {
                context.FillRectangle(brush, new Rect(RightLane, top, Lane, 2));
                last = top;
            }
        }

        if (doc.CurrentMark >= 0 && doc.CurrentMark < doc.Marks.Count)
        {
            context.FillRectangle(accent, new Rect(RightLane - 1, y(doc.Marks[doc.CurrentMark].From.Line), Lane + 1, 3));
        }
    }
}
