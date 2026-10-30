using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace Nomad.App.Ide;

/// <summary>
/// A file's text, virtualized: it measures to the whole file (inside a ScrollViewer) but lays out and draws only the
/// lines in view, so a long file opens and scrolls at once. Monospace, drawn as Visual Studio draws it (see SkiaScene),
/// with its line numbers and its box around the caret's line; tabs to four columns. A drag selects (a double-click a
/// word, a triple-click a line), Ctrl+A selects all and Ctrl+C copies. Unless its
/// <see cref="CodeDocument"/> is read-only it edits as Visual Studio does: typing, Enter keeping the indent, Tab and
/// Shift+Tab on whole lines, Ctrl+arrows by word, Ctrl+X/C/V (a whole line with nothing selected), Ctrl+Z/Y.
/// </summary>
public sealed class CodeView : Control
{
    public static readonly StyledProperty<CodeDocument?> DocumentProperty =
        AvaloniaProperty.Register<CodeView, CodeDocument?>(nameof(Document));

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

    public static readonly StyledProperty<IBrush?> CaretBrushProperty =
        AvaloniaProperty.Register<CodeView, IBrush?>(nameof(CaretBrush), Brushes.White);

    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<CodeView, Thickness>(nameof(Padding));

    private const int TabSize = 4;

    /// <summary>Longer lines are laid out only where they are in view, not whole.</summary>
    private const int WholeLineLimit = 2000;

    private CodeDocument _doc = CodeDocument.Empty;
    private List<string> _lines = [""];
    private int _longest;
    private double _charWidth;
    private Rect _viewport;
    private readonly Dictionary<int, (TextLayout Layout, bool Colored)> _layouts = [];
    private bool _selecting;

    /// <summary>The column Up and Down keep to, across shorter lines.</summary>
    private int? _goalCol;

    // The caret blinks while the text has the focus, and its timer runs only then.
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private bool _caretOn;

    static CodeView()
    {
        AffectsMeasure<CodeView>(DocumentProperty, FontFamilyProperty, FontSizeProperty, LineHeightProperty, PaddingProperty);
        AffectsRender<CodeView>(ForegroundProperty, SelectionBrushProperty, CaretBrushProperty);
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
        ResourcesChanged += (_, _) => OnThemeChanged(); // Another color theme, or dark and light.
        ActualThemeVariantChanged += (_, _) => OnThemeChanged();
        _blink.Tick += (_, _) =>
        {
            _caretOn = !_caretOn;
            InvalidateVisual();
        };

        var cut = new MenuItem { Header = "Cut", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) };
        cut.Click += (_, _) => _ = CutAsync();
        var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += (_, _) => _ = CopyAsync();
        var paste = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) };
        paste.Click += (_, _) => _ = PasteAsync();
        var all = new MenuItem { Header = "Select all", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        all.Click += (_, _) => SelectAll();
        ContextMenu = new ContextMenu { Items = { cut, copy, paste, new Separator(), all } };
        ContextMenu.Opening += (_, _) =>
        {
            cut.IsVisible = paste.IsVisible = !_doc.IsReadOnly;
            copy.IsEnabled = HasSelection || !_doc.IsReadOnly;
        };
    }

    public CodeDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
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

    public IBrush? CaretBrush
    {
        get => GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <summary>Typing goes into the text (so keys like Tab are its own, not the window's).</summary>
    public bool IsEditable => !_doc.IsReadOnly;

    private bool HasSelection => _doc.Anchor != _doc.Caret;

    /// <summary>For fonts Skia doesn't draw (the Claude app's): regular weight whatever the surroundings say.</summary>
    private Typeface Typeface => new(FontFamily, FontStyle.Normal, FontWeight.Normal);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            _doc.Changed -= OnDocumentChanged;
            _doc.Folding.Changed -= OnFoldingChanged;
            _doc = Document ?? CodeDocument.Empty;
            _doc.Changed += OnDocumentChanged;
            _doc.Folding.Changed += OnFoldingChanged;
            _chord = false;
            _goalCol = null;
            Refresh();
            RestartBlink();
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            _charWidth = 0;
            _skiaResolved = false;
            ClearLayouts();
        }
        else if (change.Property == ForegroundProperty || change.Property == LineHeightProperty)
        {
            OnThemeChanged();
        }
    }

    // The text changed (here, or by undo in another view of it): lay it out again.
    private void OnDocumentChanged()
    {
        Refresh();
        InvalidateMeasure();
        RestartBlink();
        RevealCaret();
    }

    private void Refresh()
    {
        var lines = _doc.Lines;
        _lines = new List<string>(lines.Count);
        var longest = 0;
        foreach (var raw in lines)
        {
            var line = Expand(raw);
            _lines.Add(line);
            longest = Math.Max(longest, line.Length);
        }

        _longest = longest;
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
        var raw = _doc.Lines[line];
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

    /// <summary>The column of the tab-expanded text a raw index of the line stands at.</summary>
    private int Column(TextPos pos)
    {
        var raw = _doc.Lines[pos.Line];
        var col = 0;
        for (var i = 0; i < pos.Index && i < raw.Length; i++)
        {
            col += raw[i] == '\t' ? TabSize - col % TabSize : 1;
        }

        return col;
    }

    private void ClearLayouts()
    {
        foreach (var layout in _layouts.Values)
        {
            layout.Layout.Dispose();
        }

        _layouts.Clear();
        InvalidateVisual();
    }

    // A system font is drawn by Skia itself, at Regular and subpixel-smoothed as Visual Studio's ClearType text (see
    // SkiaScene): Avalonia's text can't smooth into subpixels on the window's surface, nor pick a variable font's
    // weights (Windows' Cascadia Code and Mono are only variable).
    private const int CodeWeight = 400;

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

        // A column more than the longest line, for the caret at its end; a row per line shown (folded blocks hide theirs).
        return new Size(TextLeft + (_longest + 1) * CharWidth + p.Right, p.Top + Rows.Count * LineHeight + p.Bottom);
    }

    // The line numbers, as Visual Studio sets them: right-aligned in a column of at least three digits, 40 px in, and
    // the text 33 px after them (where its change and outlining margins are). The gutter stays put when the text
    // scrolls sideways.
    private const double GutterInset = 40;
    private const double GutterGap = 33;

    // At the gutter's left edge: the change bar (1 px) and the caret line's marker (4 px) from here, and the caret
    // line's fill right of them.
    private const double BarLeft = 6;
    private const double RowLeft = 10;

    // The fold box (Visual Studio's [-] and [+]): 9 px, centered this far right of the line numbers.
    private const double FoldBox = 9;
    private const double FoldCenter = 16;

    private int Digits => Math.Max(3, _lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);

    private double NumbersRight => Padding.Left + GutterInset + Digits * CharWidth;

    /// <summary>Where column 0 of the text is.</summary>
    private double TextLeft => NumbersRight + GutterGap;

    /// <summary>The document lines shown, one per row (folded blocks leave out all but their first line).</summary>
    private IReadOnlyList<int> Rows => _doc.Folding.Rows;

    private double RowTop(int row) => Padding.Top + row * LineHeight;

    /// <summary>Where a folded line's "..." box is, after its text.</summary>
    private Rect FoldedBox(int line, int row) =>
        new(TextLeft + (_lines[line].Length + 1) * CharWidth, RowTop(row) + 1, CharWidth * 3 + 6, LineHeight - 2);

    public override void Render(DrawingContext context)
    {
        // Hit-testable everywhere, so a press beside the text still selects.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var p = Padding;
        var left = TextLeft;
        var height = LineHeight;
        var rows = Rows;
        var view = _viewport.Height > 0 ? _viewport : new Rect(Bounds.Size);
        var firstRow = Math.Clamp((int)Math.Floor((view.Top - p.Top) / height), 0, rows.Count - 1);
        var lastRow = Math.Clamp((int)Math.Ceiling((view.Bottom - p.Top) / height), 0, rows.Count - 1);
        var charWidth = CharWidth;
        var folding = _doc.Folding;

        // Syntax colors: the lines in view are tokenized (and those above them, a frame's worth at a time).
        var highlighting = _doc.Highlighting;
        if (highlighting is not null && !highlighting.Advance(_doc.Lines, rows[lastRow], TimeSpan.FromMilliseconds(8)))
        {
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        }

        // A system font: everything under the caret is one Skia scene, drawn opaque so its text is subpixel
        // (ClearType) smoothed. Otherwise (a font of the app's own) Avalonia draws it, grey-smoothed.
        var skia = Skia;
        using var skiaFont = skia is null ? null : new SKFont(skia, (float)FontSize);
        var scene = skia is null ? null : new List<SceneItem>();
        var background = ThemeBrush("CodeBackgroundBrush");
        var numbers = ThemeBrush("CodeLineNumberBrush") ?? Foreground;

        void Fill(Rect rect, IBrush? brush)
        {
            if (brush is null || rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            if (scene is not null)
            {
                scene.Add(new SceneItem(SceneKind.Fill, rect.ToSKRect(), null, SkColor(brush)));
            }
            else
            {
                context.FillRectangle(brush, rect);
            }
        }

        void Frame(Rect rect, IBrush? brush)
        {
            Fill(new Rect(rect.X, rect.Y, rect.Width, 1), brush);
            Fill(new Rect(rect.X, rect.Bottom - 1, rect.Width, 1), brush);
            Fill(new Rect(rect.X, rect.Y, 1, rect.Height), brush);
            Fill(new Rect(rect.Right - 1, rect.Y, 1, rect.Height), brush);
        }

        void Label(string text, double x, double y, IBrush? brush)
        {
            if (scene is not null)
            {
                scene.Add(new SceneItem(SceneKind.Text, new SKRect((float)x, (float)y, 0, 0), text, SkColor(brush)));
            }
            else
            {
                using var layout = new TextLayout(text, Typeface, FontSize, brush, lineHeight: LineHeight);
                layout.Draw(context, new Point(x, y));
            }
        }

        if (scene is not null && background is not null)
        {
            Fill(view, background);
        }

        // The caret's line, filled across the view from just right of the change bar (under the selection and text).
        var caretLine = _doc.Caret.Line;
        var caretRow = folding.RowOf(caretLine);
        var caretShown = IsEditable && caretRow >= firstRow && caretRow <= lastRow;
        var row = caretShown && !HasSelection ? ThemeBrush("CodeCurrentLineBrush") : null;
        var rowRect = new Rect(view.Left + RowLeft, RowTop(caretRow), Math.Max(0, view.Width - RowLeft), height);
        Fill(rowRect, row);

        if (HasSelection && SelectionBrush is { } selection)
        {
            var (from, to) = Ordered();
            for (var r = firstRow; r <= lastRow; r++)
            {
                var line = rows[r];
                if (line < from.Line || line > to.Line)
                {
                    continue;
                }

                var start = line == from.Line ? Column(from) : 0;
                var end = line == to.Line ? Column(to) : _lines[line].Length + 1; // A selected line break shows as a space.
                if (end > start)
                {
                    Fill(new Rect(left + start * charWidth, RowTop(r), (end - start) * charWidth, height), selection);
                }
            }
        }

        var plain = SkColor(Foreground);
        for (var r = firstRow; r <= lastRow; r++)
        {
            var line = rows[r];
            var text = _lines[line];
            var y = RowTop(r);

            // A folded block: a box with "..." after its first line, as Visual Studio's.
            if (folding.IsFolded(line))
            {
                var box = FoldedBox(line, r);
                Frame(box, numbers);
                Label("...", box.X + 3, y, numbers);
            }

            if (text.Length == 0)
            {
                continue;
            }

            var runs = text.Length <= WholeLineLimit ? Runs(line, highlighting) : null;
            if (scene is not null && skiaFont!.ContainsGlyphs(text))
            {
                void Text(string piece, int col, SKColor color) =>
                    scene.Add(new SceneItem(SceneKind.Text, new SKRect((float)(left + col * charWidth), (float)y, 0, 0), piece, color));

                if (runs is not null)
                {
                    // Each run in its kind's color, from its column (the font is monospace).
                    var col = 0;
                    foreach (var (from, to, kind) in runs)
                    {
                        if (from > col)
                        {
                            Text(text[col..from], col, plain);
                        }

                        Text(text[from..to], from, SkColor(KindBrush(kind)));
                        col = to;
                    }

                    if (col < text.Length)
                    {
                        Text(text[col..], col, plain);
                    }

                    continue;
                }

                // Only the part in view of a very long line (a minified file's).
                var startCol = text.Length <= WholeLineLimit ? 0 : Math.Max(0, (int)((view.Left - left) / charWidth) - 2);
                var count = text.Length <= WholeLineLimit ? text.Length : Math.Min(text.Length - startCol, (int)(view.Width / charWidth) + 4);
                if (count > 0)
                {
                    Text(startCol == 0 && count == text.Length ? text : text.Substring(startCol, count), startCol, plain);
                }
            }
            else if (text.Length <= WholeLineLimit)
            {
                // A layout made before the line was tokenized is made again with its colors.
                if (!_layouts.TryGetValue(line, out var cached) || cached.Colored != (runs is not null))
                {
                    cached.Layout?.Dispose();
                    cached = (Layout(text, runs), runs is not null);
                    _layouts[line] = cached;
                }

                cached.Layout.Draw(context, new Point(left, y));
            }
            else
            {
                // Only the part in view of a very long line (a minified file's).
                var startCol = Math.Max(0, (int)((view.Left - left) / charWidth) - 2);
                var count = Math.Min(text.Length - startCol, (int)(view.Width / charWidth) + 4);
                if (count > 0)
                {
                    using var slice = Layout(text.Substring(startCol, count));
                    slice.Draw(context, new Point(left + startCol * charWidth, y));
                }
            }
        }

        // The gutter, over text scrolled under it, as Visual Studio's: the caret line's fill carried through it, a thin
        // bar beside lines changed since the file was opened or saved, a marker in the accent color beside the caret's
        // line, the line numbers (the caret's brighter) and the fold boxes.
        var gutter = new Rect(view.Left, view.Top, TextLeft - 2, view.Height);
        Fill(gutter, background);
        Fill(rowRect.Intersect(gutter), row);
        var change = IsEditable ? ThemeBrush("CodeChangeBrush") : null;
        for (var r = firstRow; r <= lastRow; r++)
        {
            var line = rows[r];
            if (change is not null && _doc.IsLineChanged(line))
            {
                Fill(new Rect(view.Left + BarLeft, RowTop(r), 1, height), change);
            }

            var number = (line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Label(number, view.Left + NumbersRight - number.Length * charWidth, RowTop(r), line == caretLine && IsEditable ? Foreground : numbers);

            if (folding.RegionAt(line) is not null)
            {
                var box = FoldBoxRect(view, r);
                Frame(box, numbers);
                Fill(new Rect(box.X + 2, box.Y + 4, box.Width - 4, 1), numbers);
                if (folding.IsFolded(line))
                {
                    Fill(new Rect(box.X + 4, box.Y + 2, 1, box.Height - 4), numbers);
                }
            }
        }

        if (caretShown)
        {
            Fill(new Rect(view.Left + BarLeft, RowTop(caretRow), RowLeft - BarLeft, height), ThemeBrush("AccentBrush"));
        }

        if (scene is not null)
        {
            context.Custom(new SkiaScene(new Rect(Bounds.Size), view, scene, skia!, (float)FontSize, (float)height, SkColor(background), _surface));
        }

        if (_caretOn && IsEditable && IsFocused && CaretBrush is { } caret)
        {
            context.FillRectangle(caret, CaretRect());
        }

        // Laid-out lines far out of view are let go.
        if (_layouts.Count > 600)
        {
            var (first, last) = (rows[firstRow], rows[lastRow]);
            foreach (var key in _layouts.Keys.Where(k => k < first - 100 || k > last + 100).ToList())
            {
                _layouts[key].Layout.Dispose();
                _layouts.Remove(key);
            }
        }
    }

    /// <summary>A row's fold box, in the gutter (which stays put as the text scrolls sideways).</summary>
    private Rect FoldBoxRect(Rect view, int row)
    {
        var x = Math.Round(view.Left + NumbersRight + FoldCenter - FoldBox / 2);
        var y = Math.Round(RowTop(row) + (LineHeight - FoldBox) / 2);
        return new Rect(x, y, FoldBox, FoldBox);
    }

    private static SKColor SkColor(IBrush? brush)
    {
        var color = brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;
        return new SKColor(color.R, color.G, color.B, (byte)(color.A * (brush?.Opacity ?? 1)));
    }

    // The theme's color for each kind of code (CodeKeywordBrush, â€¦), looked up again when the theme changes.
    private IBrush?[]? _kindBrushes;

    private IBrush? KindBrush(CodeKind kind)
    {
        if (_kindBrushes is null)
        {
            _kindBrushes = new IBrush?[Enum.GetValues<CodeKind>().Length];
            foreach (var k in Enum.GetValues<CodeKind>())
            {
                _kindBrushes[(int)k] = k != CodeKind.Plain && this.TryFindResource($"Code{k}Brush", ActualThemeVariant, out var value) && value is IBrush brush
                    ? brush
                    : Foreground;
            }
        }

        return _kindBrushes[(int)kind];
    }

    private readonly Dictionary<string, IBrush?> _themeBrushes = [];

    /// <summary>A theme brush by its key (null if there is none), kept until the theme changes.</summary>
    private IBrush? ThemeBrush(string key)
    {
        if (!_themeBrushes.TryGetValue(key, out var brush))
        {
            brush = this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
            _themeBrushes[key] = brush;
        }

        return brush;
    }

    // The offscreen surface the scene is drawn on, kept from frame to frame (render thread only).
    private readonly SceneSurface _surface = new();

    private void OnThemeChanged()
    {
        _kindBrushes = null;
        _themeBrushes.Clear();
        ClearLayouts();
    }

    /// <summary>A line's colored runs in columns of its tab-expanded text, or null if it hasn't been tokenized.</summary>
    private List<(int From, int To, CodeKind Kind)>? Runs(int line, CodeHighlighting? highlighting)
    {
        if (highlighting?.Spans(line) is not { } spans)
        {
            return null;
        }

        var raw = _doc.Lines[line];
        var runs = new List<(int, int, CodeKind)>(spans.Length);
        var tabs = raw.Contains('\t');
        foreach (var span in spans)
        {
            var end = Math.Min(span.Start + span.Length, raw.Length);
            if (span.Start >= end)
            {
                continue;
            }

            runs.Add(tabs ? (Column(new TextPos(line, span.Start)), Column(new TextPos(line, end)), span.Kind) : (span.Start, end, span.Kind));
        }

        return runs;
    }

    private Rect CaretRect()
    {
        var p = Padding;
        var caret = _doc.Caret;
        return new Rect(Math.Round(TextLeft + Column(caret) * CharWidth), RowTop(_doc.Folding.RowOf(caret.Line)), 1.5, LineHeight);
    }

    private TextLayout Layout(string text, List<(int From, int To, CodeKind Kind)>? runs = null) =>
        new(text, Typeface, FontSize, Foreground, lineHeight: LineHeight, textStyleOverrides: runs?
            .Select(r => new Avalonia.Utilities.ValueSpan<TextRunProperties>(r.From, r.To - r.From,
                new GenericTextRunProperties(Typeface, FontSize, foregroundBrush: KindBrush(r.Kind))))
            .ToList());

    private (TextPos From, TextPos To) Ordered() =>
        _doc.Anchor <= _doc.Caret ? (_doc.Anchor, _doc.Caret) : (_doc.Caret, _doc.Anchor);

    // The caret: blinking from on whenever it moves, while the text has the focus.
    private void RestartBlink()
    {
        // The caret never sits in a folded block: whatever hides its line unfolds.
        _doc.Folding.Reveal(_doc.Caret.Line);
        _blink.Stop();
        _caretOn = true;
        if (IsFocused && IsEditable)
        {
            _blink.Start();
        }

        InvalidateVisual();
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        RestartBlink();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _blink.Stop();
        InvalidateVisual();
    }

    // Off screen it lets go of the document, which would otherwise keep a closed group's view alive.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _doc.Changed -= OnDocumentChanged;
        _doc.Changed += OnDocumentChanged;
        _doc.Folding.Changed -= OnFoldingChanged;
        _doc.Folding.Changed += OnFoldingChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blink.Stop();
        _doc.Changed -= OnDocumentChanged;
        _doc.Folding.Changed -= OnFoldingChanged;
    }

    /// <summary>Scrolls the caret into view, once the layout has caught up with an edit.</summary>
    private void RevealCaret() =>
        Dispatcher.UIThread.Post(() =>
        {
            var rect = CaretRect();
            this.BringIntoView(rect.Inflate(new Thickness(CharWidth * 4, LineHeight, CharWidth * 4, LineHeight)));
        }, DispatcherPriority.Loaded);

    private TextPos HitTest(Point point)
    {
        var p = Padding;
        if (point.Y < p.Top)
        {
            return new TextPos(0, 0);
        }

        var rows = Rows;
        if (point.Y > p.Top + rows.Count * LineHeight)
        {
            return _doc.End;
        }

        var line = rows[Math.Clamp((int)Math.Floor((point.Y - p.Top) / LineHeight), 0, rows.Count - 1)];
        var col = Math.Clamp((int)Math.Round((point.X - TextLeft) / CharWidth), 0, _lines[line].Length);
        return new TextPos(line, RawIndex(line, col));
    }

    /// <summary>Ctrl+M was pressed: the next key finishes the chord.</summary>
    private bool _chord;

    /// <summary>A click on a fold box (in the gutter) or on a folded block's "..." toggles it; false if it was neither.</summary>
    private bool FoldClick(Point point)
    {
        var rows = Rows;
        var row = (int)Math.Floor((point.Y - Padding.Top) / LineHeight);
        if (row < 0 || row >= rows.Count)
        {
            return false;
        }

        var line = rows[row];
        var view = _viewport.Height > 0 ? _viewport : new Rect(Bounds.Size);
        var onBox = _doc.Folding.RegionAt(line) is not null && FoldBoxRect(view, row).Inflate(4).Contains(point);
        var onDots = _doc.Folding.IsFolded(line) && FoldedBox(line, row).Contains(point);
        if (!onBox && !onDots)
        {
            return false;
        }

        ToggleFold(line);
        return true;
    }

    private void ToggleFold(int line)
    {
        _doc.Seal();
        if (_doc.Folding.Toggle(line))
        {
            HideCaretFromFolds();
        }
    }

    /// <summary>A block folded over the caret: it goes to the end of the block's first line, as in Visual Studio.</summary>
    private void HideCaretFromFolds()
    {
        var rows = Rows;
        var shown = rows[_doc.Folding.RowOf(_doc.Caret.Line)];
        if (shown != _doc.Caret.Line)
        {
            _doc.Anchor = _doc.Caret = new TextPos(shown, _doc.Lines[shown].Length);
        }

        _goalCol = null;
        RestartBlink();
    }

    private void OnFoldingChanged()
    {
        InvalidateMeasure();
        InvalidateVisual();
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
        if (e.ClickCount == 1 && FoldClick(e.GetPosition(this)))
        {
            e.Handled = true;
            return;
        }

        _doc.Seal();
        _goalCol = null;
        var hit = HitTest(e.GetPosition(this));
        switch (e.ClickCount)
        {
            case 2:
                SelectWord(hit);
                break;
            case >= 3:
                _doc.Anchor = new TextPos(hit.Line, 0);
                _doc.Caret = hit.Line + 1 < _lines.Count ? new TextPos(hit.Line + 1, 0) : new TextPos(hit.Line, _doc.Lines[hit.Line].Length);
                break;
            default:
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    _doc.Anchor = hit;
                }

                _doc.Caret = hit;
                _selecting = true;
                e.Pointer.Capture(this);
                break;
        }

        RestartBlink();
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
        _doc.Caret = HitTest(point);
        this.BringIntoView(new Rect(point.X - 2, point.Y - 2, 4, 4)); // Dragging past the edge scrolls.
        RestartBlink();
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

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void SelectWord(TextPos at)
    {
        var text = _doc.Lines[at.Line];
        var start = Math.Min(at.Index, text.Length);
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

        _doc.Anchor = at with { Index = start };
        _doc.Caret = at with { Index = end };
    }

    private void SelectAll()
    {
        _doc.Seal();
        _doc.Anchor = new TextPos(0, 0);
        _doc.Caret = _doc.End;
        RestartBlink();
    }

    // Where Ctrl+Left / Ctrl+Right go: over any spaces, then over a word (or a run of other characters).
    private TextPos WordLeft(TextPos p)
    {
        if (p.Index == 0)
        {
            return p.Line > 0 ? new TextPos(p.Line - 1, _doc.Lines[p.Line - 1].Length) : p;
        }

        var text = _doc.Lines[p.Line];
        var i = p.Index;
        while (i > 0 && char.IsWhiteSpace(text[i - 1]))
        {
            i--;
        }

        if (i > 0)
        {
            var word = IsWord(text[i - 1]);
            while (i > 0 && !char.IsWhiteSpace(text[i - 1]) && IsWord(text[i - 1]) == word)
            {
                i--;
            }
        }

        return p with { Index = i };
    }

    private TextPos WordRight(TextPos p)
    {
        var text = _doc.Lines[p.Line];
        if (p.Index >= text.Length)
        {
            return p.Line + 1 < _lines.Count ? new TextPos(p.Line + 1, 0) : p;
        }

        var i = p.Index;
        if (!char.IsWhiteSpace(text[i]))
        {
            var word = IsWord(text[i]);
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && IsWord(text[i]) == word)
            {
                i++;
            }
        }

        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return p with { Index = i };
    }

    /// <summary>A line up or down, keeping to the column it started from.</summary>
    private TextPos Vertical(TextPos p, int lines)
    {
        _goalCol ??= Column(p);
        var rows = Rows;
        var line = rows[Math.Clamp(_doc.Folding.RowOf(p.Line) + lines, 0, rows.Count - 1)];
        if (line == p.Line)
        {
            return lines < 0 ? p with { Index = 0 } : p with { Index = _doc.Lines[line].Length };
        }

        return new TextPos(line, RawIndex(line, Math.Min(_goalCol.Value, _lines[line].Length)));
    }

    private int PageLines => Math.Max(1, (int)((_viewport.Height > 0 ? _viewport.Height : Bounds.Height) / LineHeight) - 1);

    /// <summary>Home goes to the line's first non-blank, or from there to its very start.</summary>
    private TextPos Home(TextPos p)
    {
        var text = _doc.Lines[p.Line];
        var indent = text.Length - text.TrimStart(' ', '\t').Length;
        return p with { Index = p.Index == indent ? 0 : indent };
    }

    private void MoveTo(TextPos to, bool extend, bool keepGoal = false)
    {
        _doc.Seal();
        if (!keepGoal)
        {
            _goalCol = null;
        }

        _doc.Caret = to;
        if (!extend)
        {
            _doc.Anchor = to;
        }

        RestartBlink();
        RevealCaret();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            return;
        }

        // Visual Studio's outlining chords: Ctrl+M, Ctrl+M folds or unfolds the block at the caret; Ctrl+M, Ctrl+L all of them.
        if (_chord)
        {
            _chord = false;
            if (ctrl && e.Key == Key.M && _doc.Folding.Innermost(_doc.Caret.Line) is { } block)
            {
                ToggleFold(block.Start);
            }
            else if (ctrl && e.Key == Key.L)
            {
                _doc.Folding.ToggleAll();
                HideCaretFromFolds();
            }

            e.Handled = true;
            return;
        }

        var caret = _doc.Caret;
        var (from, to) = Ordered();
        var editable = IsEditable;
        switch (e.Key)
        {
            case Key.M when ctrl && !shift:
                _chord = true;
                break;
            case Key.A when ctrl && !shift:
                SelectAll();
                break;
            case Key.C or Key.Insert when ctrl && !shift:
                _ = CopyAsync();
                break;
            case Key.X when ctrl && !shift && editable:
                _ = CutAsync();
                break;
            case Key.V when ctrl && !shift && editable:
            case Key.Insert when shift && !ctrl && editable:
                _ = PasteAsync();
                break;
            case Key.Z when ctrl && !shift && editable:
                _doc.Undo();
                break;
            case Key.Y when ctrl && !shift && editable:
            case Key.Z when ctrl && shift && editable:
                _doc.Redo();
                break;

            case Key.Left:
                MoveTo(HasSelection && !shift && !ctrl ? from : ctrl ? WordLeft(caret) : Step(caret, -1), shift);
                break;
            case Key.Right:
                MoveTo(HasSelection && !shift && !ctrl ? to : ctrl ? WordRight(caret) : Step(caret, 1), shift);
                break;
            case Key.Up when !ctrl:
                MoveTo(Vertical(caret, -1), shift, keepGoal: true);
                break;
            case Key.Down when !ctrl:
                MoveTo(Vertical(caret, 1), shift, keepGoal: true);
                break;
            case Key.PageUp when !ctrl:
                MoveTo(Vertical(caret, -PageLines), shift, keepGoal: true);
                break;
            case Key.PageDown when !ctrl:
                MoveTo(Vertical(caret, PageLines), shift, keepGoal: true);
                break;
            case Key.Home:
                MoveTo(ctrl ? new TextPos(0, 0) : Home(caret), shift);
                break;
            case Key.End:
                MoveTo(ctrl ? _doc.End : caret with { Index = _doc.Lines[caret.Line].Length }, shift);
                break;

            case Key.Back when editable && !shift:
                _goalCol = null;
                _doc.Replace(HasSelection ? from : ctrl ? WordLeft(caret) : Step(caret, -1), HasSelection ? to : caret, "");
                break;
            case Key.Delete when editable && !shift:
                _goalCol = null;
                _doc.Replace(HasSelection ? from : caret, HasSelection ? to : ctrl ? WordRight(caret) : Step(caret, 1), "");
                break;
            case Key.Enter when editable && !ctrl:
                _goalCol = null;
                _doc.Seal();
                var line = _doc.Lines[from.Line];
                var indent = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
                _doc.Replace(from, to, "\n" + indent[..Math.Min(indent.Length, from.Index)]);
                _doc.Seal();
                break;
            case Key.Tab when editable && !ctrl:
                _goalCol = null;
                Indent(outdent: shift);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>A character left or right, over line ends.</summary>
    private TextPos Step(TextPos p, int by)
    {
        if (by < 0)
        {
            return p.Index > 0 ? p with { Index = p.Index - 1 } : Neighbor(p.Line, -1) is { } above ? new TextPos(above, _doc.Lines[above].Length) : p;
        }

        return p.Index < _doc.Lines[p.Line].Length ? p with { Index = p.Index + 1 } : Neighbor(p.Line, 1) is { } below ? new TextPos(below, 0) : p;
    }

    /// <summary>The line shown above or below this one (over a folded block, as Visual Studio steps over it).</summary>
    private int? Neighbor(int line, int by)
    {
        var rows = Rows;
        var row = _doc.Folding.RowOf(line) + by;
        return row >= 0 && row < rows.Count ? rows[row] : null;
    }

    private string IndentUnit => _doc.IndentWithTabs ? "\t" : new string(' ', TabSize);

    /// <summary>
    /// Tab: in a line, spaces (or a tab) to the next stop; over lines, each of them indented. Shift+Tab takes a level of
    /// indent off each line the selection or caret is on. Either way one undo step.
    /// </summary>
    private void Indent(bool outdent)
    {
        var (from, to) = Ordered();
        if (!outdent && from.Line == to.Line)
        {
            var unit = _doc.IndentWithTabs ? "\t" : new string(' ', TabSize - Column(from) % TabSize);
            _doc.Seal();
            _doc.Replace(from, to, unit);
            _doc.Seal();
            return;
        }

        // A selection ending at a line's very start leaves that line alone.
        var lastLine = to.Line > from.Line && to.Index == 0 ? to.Line - 1 : to.Line;
        var lines = new List<string>();
        var shifts = new List<int>();
        for (var i = from.Line; i <= lastLine; i++)
        {
            var text = _doc.Lines[i];
            if (outdent)
            {
                var cut = text.StartsWith('\t') ? 1 : text.Length - text.TrimStart(' ').Length is var spaces && spaces > 0 ? Math.Min(spaces, TabSize) : 0;
                lines.Add(text[cut..]);
                shifts.Add(-cut);
            }
            else
            {
                var add = text.Length == 0 ? "" : IndentUnit;
                lines.Add(add + text);
                shifts.Add(add.Length);
            }
        }

        if (shifts.All(s => s == 0))
        {
            return;
        }

        var anchor = _doc.Anchor;
        var caret = _doc.Caret;
        _doc.Seal();
        _doc.Replace(new TextPos(from.Line, 0), new TextPos(lastLine, _doc.Lines[lastLine].Length), string.Join('\n', lines));
        _doc.Seal();

        TextPos Shift(TextPos p) =>
            p.Line < from.Line || p.Line > lastLine ? p : p with { Index = Math.Max(0, p.Index + shifts[p.Line - from.Line]) };

        // Whole lines stay selected, as they were.
        _doc.Anchor = Shift(anchor);
        _doc.Caret = Shift(caret);
        InvalidateVisual();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!IsEditable || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }

        var (from, to) = Ordered();
        _goalCol = null;
        _doc.Replace(from, to, e.Text);
        e.Handled = true;
    }

    /// <summary>The selected text, with line breaks as the clipboard wants them.</summary>
    private string SelectedText()
    {
        var (from, to) = Ordered();
        return _doc.GetText(from, to).Replace("\n", Environment.NewLine);
    }

    /// <summary>With nothing selected, Ctrl+C and Ctrl+X take the caret's whole line.</summary>
    private (TextPos From, TextPos To) LineSpan()
    {
        var line = _doc.Caret.Line;
        return line + 1 < _lines.Count
            ? (new TextPos(line, 0), new TextPos(line + 1, 0))
            : (line > 0 ? new TextPos(line - 1, _doc.Lines[line - 1].Length) : new TextPos(line, 0), _doc.End);
    }

    private async Task CopyAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        if (HasSelection)
        {
            await clipboard.SetTextAsync(SelectedText());
        }
        else if (IsEditable)
        {
            var line = _doc.Lines[_doc.Caret.Line];
            await clipboard.SetTextAsync(line + Environment.NewLine);
        }
    }

    private async Task CutAsync()
    {
        if (!IsEditable || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var (from, to) = HasSelection ? Ordered() : LineSpan();
        await clipboard.SetTextAsync(HasSelection ? SelectedText() : _doc.Lines[_doc.Caret.Line] + Environment.NewLine);
        _doc.Seal();
        _doc.Replace(from, to, "");
        _doc.Seal();
    }

    private async Task PasteAsync()
    {
        if (!IsEditable || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || await clipboard.TryGetTextAsync() is not { Length: > 0 } text)
        {
            return;
        }

        var (from, to) = Ordered();
        _doc.Seal();
        _doc.Replace(from, to, text);
        _doc.Seal();
    }
}

internal enum SceneKind : byte
{
    Fill,
    Text,
}

/// <summary>A fill (its rectangle) or a piece of text (at the rectangle's left and top) in one color.</summary>
internal readonly record struct SceneItem(SceneKind Kind, SKRect Rect, string? Text, SKColor Color);

/// <summary>The offscreen surface a <see cref="SkiaScene"/> draws on, made again only when its size changes.</summary>
internal sealed class SceneSurface
{
    private SKSurface? _surface;
    private SKSizeI _size;

    public SKSurface? Get(SKSizeI size)
    {
        if (_surface is null || _size != size)
        {
            _surface?.Dispose();

            // In memory and in the platform's own BGRA: Skia smooths text into subpixels only there (in RGBA, or on the
            // GPU, it quietly goes grey). Only the part in view is drawn, so handing it to the GPU each frame is cheap.
            var info = new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);

            // RGB stripes, as nearly every screen has: Skia smooths text into them (ClearType's subpixel smoothing).
            var props = new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal);
            _surface = SKSurface.Create(info, props);
            _size = size;
        }

        return _surface;
    }
}

/// <summary>
/// The code's background, selection, text and gutter, drawn onto an opaque surface of their own and then onto the
/// window: Skia smooths text into the subpixels only where it knows what is under it, which on the window's own
/// (layered, partly transparent) surface it doesn't. A system font at Regular, as Visual Studio's.
/// </summary>
internal sealed class SkiaScene(Rect bounds, Rect view, List<SceneItem> items, SKTypeface typeface, float size, float lineHeight, SKColor background, SceneSurface surface)
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
        var canvas = lease.SkCanvas;
        var matrix = canvas.TotalMatrix;

        // The part in view, in the window's pixels.
        var device = matrix.MapRect(view.ToSKRect());
        var area = SKRectI.Round(device);
        area.Intersect(canvas.DeviceClipBounds);
        if (area.Width <= 0 || area.Height <= 0 || surface.Get(new SKSizeI(area.Width, area.Height)) is not { } target)
        {
            return;
        }

        var c = target.Canvas;
        c.ResetMatrix();
        c.Clear(background.Alpha == 255 ? background : SKColors.Black);
        c.Translate(-area.Left, -area.Top);
        c.Concat(in matrix);

        using var font = new SKFont(typeface, size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true, Hinting = SKFontHinting.Slight };
        using var paint = new SKPaint { IsAntialias = true };
        var metrics = font.Metrics;
        var baseline = (lineHeight - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent;
        foreach (var item in items)
        {
            paint.Color = item.Color;
            if (item.Kind == SceneKind.Fill)
            {
                c.DrawRect(item.Rect, paint);
            }
            else
            {
                c.DrawText(item.Text, item.Rect.Left, item.Rect.Top + baseline, SKTextAlign.Left, font, paint);
            }
        }

        c.Flush();
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawSurface(target, area.Left, area.Top);
        canvas.Restore();
    }
}
