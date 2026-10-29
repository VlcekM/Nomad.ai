using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Nomad.App.Ide;

/// <summary>
/// A file's text, virtualized: it measures to the whole file (inside a ScrollViewer) but lays out and draws only the
/// lines in view, so a long file opens and scrolls at once. Monospace and light (see CodeWeight), tabs to four columns; a drag
/// selects (a double-click a word, a triple-click a line), Ctrl+A selects all and Ctrl+C copies.
/// </summary>
public sealed class CodeView : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeView, string?>(nameof(Text));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<CodeView>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<CodeView>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<CodeView>();

    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<CodeView, double>(nameof(LineHeight), 17);

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<CodeView, IBrush?>(nameof(SelectionBrush));

    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<CodeView, Thickness>(nameof(Padding));

    private const int TabSize = 4;

    /// <summary>Longer lines are laid out only where they are in view, not whole.</summary>
    private const int WholeLineLimit = 2000;

    private string[] _raw = [];
    private string[] _lines = [];
    private int _longest;
    private double _charWidth;
    private Rect _viewport;
    private readonly Dictionary<int, TextLayout> _layouts = [];

    // The selection, in (line, column) of the tab-expanded text: from where it started to where it is now.
    private (int Line, int Col) _anchor;
    private (int Line, int Col) _caret;
    private bool _selecting;

    static CodeView()
    {
        AffectsMeasure<CodeView>(TextProperty, FontFamilyProperty, FontSizeProperty, LineHeightProperty, PaddingProperty);
        AffectsRender<CodeView>(ForegroundProperty, SelectionBrushProperty);
        FocusableProperty.OverrideDefaultValue<CodeView>(true);
    }

    public CodeView()
    {
        Cursor = new Cursor(StandardCursorType.Ibeam);
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            InvalidateVisual();
        };

        var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += (_, _) => _ = CopyAsync();
        var all = new MenuItem { Header = "Select all", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        all.Click += (_, _) => SelectAll();
        ContextMenu = new ContextMenu { Items = { copy, all } };
        ContextMenu.Opening += (_, _) => copy.IsEnabled = HasSelection;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    private bool HasSelection => _anchor != _caret;

    /// <summary>For fonts Skia doesn't draw (the Claude app's): regular weight whatever the surroundings say.</summary>
    private Typeface Typeface => new(FontFamily, FontStyle.Normal, FontWeight.Normal);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            SetText(Text ?? "");
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            _charWidth = 0;
            _skiaResolved = false;
            ClearLayouts();
        }
        else if (change.Property == ForegroundProperty || change.Property == LineHeightProperty)
        {
            ClearLayouts();
        }
    }

    private void SetText(string text)
    {
        _raw = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        _lines = _raw.Select(Expand).ToArray();
        _longest = _lines.Length == 0 ? 0 : _lines.Max(l => l.Length);
        _anchor = _caret = default;
        ClearLayouts();
    }

    private static string Expand(string line)
    {
        if (!line.Contains('\t'))
        {
            return line;
        }

        var sb = new System.Text.StringBuilder(line.Length + 16);
        foreach (var c in line)
        {
            if (c == '\t')
            {
                sb.Append(' ', TabSize - sb.Length % TabSize);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>The raw line's index for a column of its tab-expanded text.</summary>
    private int RawIndex(int line, int col)
    {
        var raw = _raw[line];
        var at = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var width = raw[i] == '\t' ? TabSize - at % TabSize : 1;
            if (at + width > col)
            {
                return col - at < width / 2.0 || raw[i] != '\t' ? i : i + 1;
            }

            at += width;
        }

        return raw.Length;
    }

    private void ClearLayouts()
    {
        foreach (var layout in _layouts.Values)
        {
            layout.Dispose();
        }

        _layouts.Clear();
        InvalidateVisual();
    }

    // A system font is drawn by Skia itself, Light: Regular, smoothed in grey on the dark card, reads bold next to
    // Visual Studio's ClearType, and Avalonia's text can't pick a variable font's other weights (Windows' Cascadia Code
    // and Mono are only variable). A font without a Light falls back to its nearest weight.
    private const int CodeWeight = 300;

    private SKTypeface? _skia;
    private bool _skiaResolved;

    private SKTypeface? Skia
    {
        get
        {
            if (!_skiaResolved)
            {
                _skiaResolved = true;
                _skia = null;
                foreach (var name in FontFamily.FamilyNames)
                {
                    if (name.Contains('#') || name.Contains(':'))
                    {
                        break; // A font of the app's own collection (the Claude app's): Avalonia's text draws it.
                    }

                    if (SKFontManager.Default.MatchFamily(name, new SKFontStyle(CodeWeight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)) is { } typeface &&
                        string.Equals(typeface.FamilyName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        _skia = typeface;
                        break;
                    }
                }
            }

            return _skia;
        }
    }

    private double CharWidth
    {
        get
        {
            if (_charWidth <= 0 && Skia is { } skia)
            {
                using var font = new SKFont(skia, (float)FontSize);
                _charWidth = font.MeasureText("0000000000") / 10;
            }

            if (_charWidth <= 0)
            {
                using var probe = new TextLayout("0000000000", Typeface, FontSize, null);
                _charWidth = probe.WidthIncludingTrailingWhitespace / 10;
            }

            return _charWidth;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var p = Padding;
        return new Size(p.Left + _longest * CharWidth + p.Right, p.Top + _lines.Length * LineHeight + p.Bottom);
    }

    public override void Render(DrawingContext context)
    {
        // Hit-testable everywhere, so a press beside the text still selects.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_lines.Length == 0)
        {
            return;
        }

        var p = Padding;
        var height = LineHeight;
        var view = _viewport.Height > 0 ? _viewport : new Rect(Bounds.Size);
        var first = Math.Clamp((int)Math.Floor((view.Top - p.Top) / height), 0, _lines.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling((view.Bottom - p.Top) / height), 0, _lines.Length - 1);
        var charWidth = CharWidth;

        if (HasSelection && SelectionBrush is { } selection)
        {
            var (from, to) = Ordered();
            for (var line = Math.Max(first, from.Line); line <= Math.Min(last, to.Line); line++)
            {
                var start = line == from.Line ? from.Col : 0;
                var end = line == to.Line ? to.Col : _lines[line].Length + 1; // A selected line break shows as a space.
                if (end > start)
                {
                    context.FillRectangle(selection, new Rect(p.Left + start * charWidth, p.Top + line * height, (end - start) * charWidth, height));
                }
            }
        }

        var skia = Skia;
        using var skiaFont = skia is null ? null : new SKFont(skia, (float)FontSize);
        var skiaLines = new List<(string Text, float X, float Y)>();
        for (var line = first; line <= last; line++)
        {
            var text = _lines[line];
            if (text.Length == 0)
            {
                continue;
            }

            var y = p.Top + line * height;
            if (skiaFont is not null && skiaFont.ContainsGlyphs(text))
            {
                // Only the part in view of a very long line (a minified file's).
                var startCol = text.Length <= WholeLineLimit ? 0 : Math.Max(0, (int)((view.Left - p.Left) / charWidth) - 2);
                var count = text.Length <= WholeLineLimit ? text.Length : Math.Min(text.Length - startCol, (int)(view.Width / charWidth) + 4);
                if (count > 0)
                {
                    skiaLines.Add((startCol == 0 && count == text.Length ? text : text.Substring(startCol, count), (float)(p.Left + startCol * charWidth), (float)y));
                }
            }
            else if (text.Length <= WholeLineLimit)
            {
                if (!_layouts.TryGetValue(line, out var layout))
                {
                    layout = Layout(text);
                    _layouts[line] = layout;
                }

                layout.Draw(context, new Point(p.Left, y));
            }
            else
            {
                // Only the part in view of a very long line (a minified file's).
                var startCol = Math.Max(0, (int)((view.Left - p.Left) / charWidth) - 2);
                var count = Math.Min(text.Length - startCol, (int)(view.Width / charWidth) + 4);
                if (count > 0)
                {
                    using var slice = Layout(text.Substring(startCol, count));
                    slice.Draw(context, new Point(p.Left + startCol * charWidth, y));
                }
            }
        }

        if (skia is not null && skiaLines.Count > 0)
        {
            var color = Foreground is ISolidColorBrush brush ? brush.Color : Colors.Gray;
            var opacity = Foreground?.Opacity ?? 1;
            context.Custom(new SkiaLines(new Rect(Bounds.Size), skiaLines, skia, (float)FontSize, (float)height,
                new SKColor(color.R, color.G, color.B, (byte)(color.A * opacity))));
        }

        // Laid-out lines far out of view are let go.
        if (_layouts.Count > 600)
        {
            foreach (var key in _layouts.Keys.Where(k => k < first - 100 || k > last + 100).ToList())
            {
                _layouts[key].Dispose();
                _layouts.Remove(key);
            }
        }
    }

    private TextLayout Layout(string text) =>
        new(text, Typeface, FontSize, Foreground, lineHeight: LineHeight);

    private ((int Line, int Col) From, (int Line, int Col) To) Ordered() =>
        _anchor.Line < _caret.Line || (_anchor.Line == _caret.Line && _anchor.Col <= _caret.Col) ? (_anchor, _caret) : (_caret, _anchor);

    private (int Line, int Col) HitTest(Point point)
    {
        if (_lines.Length == 0)
        {
            return default;
        }

        var p = Padding;
        var line = Math.Clamp((int)Math.Floor((point.Y - p.Top) / LineHeight), 0, _lines.Length - 1);
        if (point.Y < p.Top)
        {
            return (0, 0);
        }

        if (point.Y > p.Top + _lines.Length * LineHeight)
        {
            return (_lines.Length - 1, _lines[^1].Length);
        }

        var col = Math.Clamp((int)Math.Round((point.X - p.Left) / CharWidth), 0, _lines[line].Length);
        return (line, col);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        var hit = HitTest(e.GetPosition(this));
        switch (e.ClickCount)
        {
            case 2:
                SelectWord(hit);
                break;
            case >= 3:
                _anchor = (hit.Line, 0);
                _caret = hit.Line + 1 < _lines.Length ? (hit.Line + 1, 0) : (hit.Line, _lines[hit.Line].Length);
                break;
            default:
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    _anchor = hit;
                }

                _caret = hit;
                _selecting = true;
                e.Pointer.Capture(this);
                break;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting)
        {
            return;
        }

        var point = e.GetPosition(this);
        _caret = HitTest(point);
        this.BringIntoView(new Rect(point.X - 2, point.Y - 2, 4, 4)); // Dragging past the edge scrolls.
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _selecting = false;
    }

    private void SelectWord((int Line, int Col) at)
    {
        var text = _lines[at.Line];
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var start = Math.Min(at.Col, text.Length);
        var end = start;
        if (start < text.Length && IsWord(text[start]) || start > 0 && IsWord(text[start - 1]))
        {
            while (start > 0 && IsWord(text[start - 1]))
            {
                start--;
            }

            while (end < text.Length && IsWord(text[end]))
            {
                end++;
            }
        }
        else if (start < text.Length)
        {
            end = start + 1;
        }

        _anchor = (at.Line, start);
        _caret = (at.Line, end);
    }

    private void SelectAll()
    {
        if (_lines.Length == 0)
        {
            return;
        }

        _anchor = (0, 0);
        _caret = (_lines.Length - 1, _lines[^1].Length);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers != KeyModifiers.Control)
        {
            return;
        }

        if (e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key is Key.C or Key.Insert)
        {
            _ = CopyAsync();
            e.Handled = true;
        }
    }

    /// <summary>The selected text, with its tabs and line breaks as in the file.</summary>
    private string SelectedText()
    {
        var (from, to) = Ordered();
        var sb = new System.Text.StringBuilder();
        for (var line = from.Line; line <= to.Line; line++)
        {
            var start = line == from.Line ? RawIndex(line, from.Col) : 0;
            var end = line == to.Line ? RawIndex(line, to.Col) : _raw[line].Length;
            sb.Append(_raw[line], start, Math.Max(0, end - start));
            if (line < to.Line)
            {
                sb.Append(Environment.NewLine);
            }
        }

        return sb.ToString();
    }

    private async Task CopyAsync()
    {
        if (HasSelection && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(SelectedText());
        }
    }
}

/// <summary>Lines drawn straight onto Skia's canvas, in a system font at its regular weight.</summary>
internal sealed class SkiaLines(Rect bounds, List<(string Text, float X, float Y)> lines, SKTypeface typeface, float size, float lineHeight, SKColor color)
    : ICustomDrawOperation
{
    public Rect Bounds => bounds;

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
        {
            return;
        }

        using var lease = feature.Lease();
        using var font = new SKFont(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true, Hinting = SKFontHinting.Slight };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        var metrics = font.Metrics;
        var baseline = (lineHeight - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent;
        foreach (var (text, x, y) in lines)
        {
            lease.SkCanvas.DrawText(text, x, y + baseline, SKTextAlign.Left, font, paint);
        }
    }
}
