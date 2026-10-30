using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Nomad.Core.Code;
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
        Cursor = TextCursor;
        EffectiveViewportChanged += (_, e) =>
        {
            // Scrolling takes the Quick Info away (the name has moved from under it).
            if (e.EffectiveViewport.Position != _viewport.Position)
            {
                HideQuickInfo();
                _completion?.Reposition();
            }

            _viewport = e.EffectiveViewport;
            InvalidateVisual();
        };
        ResourcesChanged += (_, _) => OnThemeChanged(); // Another color theme, or dark and light.
        ActualThemeVariantChanged += (_, _) => OnThemeChanged();
        _quickTimer.Tick += (_, _) => ShowQuickInfo();
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
        var ask = new MenuItem { Header = "Ask about this", InputGesture = new KeyGesture(Key.I, KeyModifiers.Control) };
        ask.Click += (_, _) => Ask();
        var definition = new MenuItem { Header = "Go to definition", InputGesture = new KeyGesture(Key.F12) };
        Reference? definitionAt = null;
        definition.Click += (_, _) =>
        {
            if (definitionAt is { } at)
            {
                RequestDefinition(at);
            }
        };
        var implementation = new MenuItem { Header = "Go to implementation", InputGesture = new KeyGesture(Key.F12, KeyModifiers.Control) };
        implementation.Click += (_, _) =>
        {
            if (definitionAt is { } at)
            {
                RequestImplementation(at);
            }
        };
        var references = new MenuItem { Header = "Find all references", InputGesture = new KeyGesture(Key.F12, KeyModifiers.Shift) };
        references.Click += (_, _) =>
        {
            if (definitionAt is { } at)
            {
                Navigator?.FindReferences(Spot(at));
            }
        };
        var rename = new MenuItem { Header = "Rename…", InputGesture = new KeyGesture(Key.F2) };
        rename.Click += (_, _) =>
        {
            if (definitionAt is { } at)
            {
                Navigator?.Rename(Spot(at));
            }
        };
        ContextMenu = new ContextMenu { Items = { ask, definition, implementation, references, rename, new Separator(), cut, copy, paste, new Separator(), all } };
        ContextMenu.Opening += (_, _) =>
        {
            cut.IsVisible = paste.IsVisible = !_doc.IsReadOnly;
            copy.IsEnabled = HasSelection || !_doc.IsReadOnly;
            ask.IsVisible = AskRequested is not null;
            definitionAt = HasSelection ? null : ReferenceAt(_doc.Caret);
            definition.IsVisible = implementation.IsVisible = references.IsVisible = Navigator is not null && definitionAt is not null;
            rename.IsVisible = definition.IsVisible && !_doc.IsReadOnly;
        };

        _completion = new CodeCompletion(this, popup => LogicalChildren.Add(popup));
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
            _doc.CaretChanged -= OnCaretMoved;
            _doc.RevealRequested -= OnRevealRequested;
            _doc.MarksChanged -= InvalidateVisual;
            _doc = Document ?? CodeDocument.Empty;
            _doc.MarksChanged += InvalidateVisual;
            _doc.Changed += OnDocumentChanged;
            _doc.Folding.Changed += OnFoldingChanged;
            _doc.RevealRequested += OnRevealRequested;
            _doc.CaretChanged += OnCaretMoved;
            _completion.Close();
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

    // The git margin: at the gutter's left edge, before the change bar.
    private const double GitBarLeft = 1;
    private const double GitBarWidth = 3;
    private static readonly IBrush GitModifiedBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x8C, 0xD8));

    // Lines another program just changed: an accent wash fading out over this long.
    private const double FlashMs = 1600;
    private DispatcherTimer? _flashTimer;
    private const double RowLeft = 10;

    // The fold button: 16 px, centered this far right of the line numbers, its chevron 10 px.
    private const double FoldButton = 16;
    private const double FoldCenter = 16;
    private const double FoldGlyph = 10;

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

        // A rounded fill or a chevron (the fold buttons').
        void Shape(SceneKind kind, Rect rect, IBrush? brush)
        {
            if (brush is null)
            {
                return;
            }

            if (scene is not null)
            {
                scene.Add(new SceneItem(kind, rect.ToSKRect(), null, SkColor(brush)));
            }
            else if (kind == SceneKind.RoundFill)
            {
                context.DrawRectangle(brush, null, rect, 3, 3);
            }
            else
            {
                var pen = new Pen(brush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                var (a, b, c) = kind == SceneKind.ChevronDown
                    ? (new Point(rect.Left, rect.Center.Y - rect.Height / 4), new Point(rect.Center.X, rect.Center.Y + rect.Height / 4), new Point(rect.Right, rect.Center.Y - rect.Height / 4))
                    : (new Point(rect.Center.X - rect.Width / 4, rect.Top), new Point(rect.Center.X + rect.Width / 4, rect.Center.Y), new Point(rect.Center.X - rect.Width / 4, rect.Bottom));
                context.DrawLine(pen, a, b);
                context.DrawLine(pen, b, c);
            }
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

        // Lines another program (the agent) just changed: an accent wash that fades.
        var flashAge = (DateTime.UtcNow - _doc.FlashedUtc).TotalMilliseconds;
        if (_doc.FlashLines.Count > 0 && flashAge < FlashMs && ThemeBrush("AccentBrush") is ISolidColorBrush flashAccent)
        {
            var strength = 0.32 * (1 - Math.Pow(Math.Min(1, flashAge / FlashMs), 2));
            var wash = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(flashAccent.Color, strength);
            foreach (var flashed in _doc.FlashLines)
            {
                var flashRow = folding.RowOf(Math.Min(flashed, _lines.Count - 1));
                if (flashRow >= firstRow && flashRow <= lastRow && rows[flashRow] == flashed)
                {
                    Fill(new Rect(view.Left + RowLeft, RowTop(flashRow), Math.Max(0, view.Width - RowLeft), height), wash);
                }
            }

            // Repainted until it has faded (the timer stops itself then).
            if (_flashTimer is null)
            {
                _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _flashTimer.Tick += (_, _) =>
                {
                    InvalidateVisual();
                    if ((DateTime.UtcNow - _doc.FlashedUtc).TotalMilliseconds >= FlashMs)
                    {
                        _flashTimer.Stop();
                    }
                };
            }

            if (!_flashTimer.IsEnabled)
            {
                _flashTimer.Start();
            }
        }

        // The find's matches in view (they are in document order: the first in view is found by halving).
        var marks = _doc.Marks;
        if (marks.Count > 0 && ThemeBrush("CodeFindMatchBrush") is { } markBrush)
        {
            int lo = 0, hi = marks.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (marks[mid].From.Line < rows[firstRow])
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (var i = lo; i < marks.Count && marks[i].From.Line <= rows[lastRow]; i++)
            {
                var (from, to) = marks[i];
                var markRow = folding.RowOf(from.Line);
                if (rows[markRow] == from.Line)
                {
                    var start = Column(from);
                    Fill(new Rect(left + start * charWidth, RowTop(markRow), (Column(to) - start) * charWidth, height), markBrush);
                }
            }
        }

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

        // Ctrl over a name that leads somewhere: underlined in the accent, easing in.
        if (_link is { } link && link.Line < _lines.Count && folding.RowOf(link.Line) is var linkRow &&
            linkRow >= firstRow && linkRow <= lastRow && rows[linkRow] == link.Line && ThemeBrush("AccentBrush") is ISolidColorBrush linkAccent)
        {
            var linkP = Math.Min(1, (DateTime.UtcNow - _linkUtc).TotalMilliseconds / LinkFadeMs);
            var underline = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(linkAccent.Color, 1 - Math.Pow(1 - linkP, 3));
            var start = Column(new TextPos(link.Line, link.Start));
            var end = Column(new TextPos(link.Line, link.End));
            Fill(new Rect(left + start * charWidth, RowTop(linkRow) + height - 3, (end - start) * charWidth, 1), underline);
            if (linkP < 1)
            {
                TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ => InvalidateVisual());
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

        // The language server's errors (red) and warnings (green) as wavy underlines, its suggestions as a few grey dots
        // under their start, as Visual Studio's; fading in when they come.
        if (_doc.Diagnostics is { Count: > 0 } diagnostics)
        {
            var fading = false;
            var (firstLine, lastLine) = (rows[firstRow], rows[lastRow]);
            foreach (var (d, since) in diagnostics)
            {
                var fade = Math.Min(1, (DateTime.UtcNow - since).TotalMilliseconds / DiagnosticFadeMs);
                var eased = 1 - Math.Pow(1 - fade, 3);
                fading |= fade < 1;
                if (d.EndLine < firstLine || d.Line > lastLine || d.Line >= _lines.Count)
                {
                    continue;
                }

                var brush = d.Severity switch
                {
                    Nomad.Core.Lsp.LspSeverity.Error => ThemeBrush("DangerBrush"),
                    Nomad.Core.Lsp.LspSeverity.Warning => WarningBrush,
                    Nomad.Core.Lsp.LspSeverity.Information => numbers,
                    _ => null,
                };
                if (brush is not ISolidColorBrush solid)
                {
                    continue;
                }

                var color = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(solid.Color, solid.Opacity * eased);
                for (var l = Math.Max(d.Line, firstLine); l <= Math.Min(d.EndLine, Math.Min(lastLine, _lines.Count - 1)); l++)
                {
                    var dRow = folding.RowOf(l);
                    if (rows[dRow] != l)
                    {
                        continue;
                    }

                    var length = _lines[l].Length;
                    var from = l == d.Line ? Math.Min(d.Column, length) : _lines[l].Length - _lines[l].TrimStart().Length;
                    var to = l == d.EndLine ? Math.Min(d.EndColumn, length) : length;
                    if (to <= from)
                    {
                        // An empty range (a missing ';'): the character before it, or a little stub.
                        (from, to) = from > 0 ? (from - 1, from) : (from, from + 1);
                    }

                    var x0 = left + Column(new TextPos(l, from)) * charWidth;
                    var x1 = left + Math.Max(Column(new TextPos(l, to)), Column(new TextPos(l, from)) + 1) * charWidth;
                    var bottom = RowTop(dRow) + height - 1;
                    if (d.Severity == Nomad.Core.Lsp.LspSeverity.Information)
                    {
                        for (var x = x0; x < Math.Min(x1, x0 + 2 * charWidth); x += 3)
                        {
                            Fill(new Rect(Math.Round(x), bottom - 1, 1, 1), color);
                        }

                        break;
                    }

                    var wave = new Rect(x0, bottom - 3, x1 - x0, 3);
                    if (scene is not null)
                    {
                        scene.Add(new SceneItem(SceneKind.Squiggle, wave.ToSKRect(), null, SkColor(color)));
                    }
                    else
                    {
                        using var path = SquigglePath(wave.ToSKRect());
                        var points = path.Points;
                        for (var i = 1; i < points.Length; i++)
                        {
                            context.DrawLine(new Pen(color, 1), new Point(points[i - 1].X, points[i - 1].Y), new Point(points[i].X, points[i].Y));
                        }
                    }
                }
            }

            if (fading)
            {
                TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ => InvalidateVisual());
            }
        }

        // The gutter, over text scrolled under it, as Visual Studio's: the caret line's fill carried through it, a thin
        // bar beside lines changed since the file was opened or saved, a marker in the accent color beside the caret's
        // line, the line numbers (the caret's brighter) and the fold boxes.
        var gutter = new Rect(view.Left, view.Top, TextLeft - 2, view.Height);
        Fill(gutter, background);
        Fill(rowRect.Intersect(gutter), row);
        var change = IsEditable ? ThemeBrush("CodeChangeBrush") : null;

        // The git margin, at the gutter's very edge as Visual Studio's: lines added since the last commit in green,
        // changed ones in blue, and a red notch where lines were removed.
        if (_doc.GitHunks is { Count: > 0 } hunks)
        {
            var added = ThemeBrush("DiffAddBrush");
            var removed = ThemeBrush("DiffDelBrush");
            foreach (var hunk in hunks)
            {
                if (hunk.NewCount == 0)
                {
                    var at = Math.Min(hunk.NewStart, _lines.Count - 1);
                    var notchRow = folding.RowOf(at);
                    if (notchRow >= firstRow && notchRow <= lastRow)
                    {
                        Fill(new Rect(view.Left + GitBarLeft, RowTop(notchRow) - (hunk.NewStart >= _lines.Count ? -height + 2 : 1), GitBarWidth + 1, 3), removed);
                    }

                    continue;
                }

                for (var l = hunk.NewStart; l < hunk.NewStart + hunk.NewCount && l < _lines.Count; l++)
                {
                    var hunkRow = folding.RowOf(l);
                    if (hunkRow >= firstRow && hunkRow <= lastRow && rows[hunkRow] == l)
                    {
                        Fill(new Rect(view.Left + GitBarLeft, RowTop(hunkRow), GitBarWidth, height), hunk.OldCount == 0 ? added : GitModifiedBrush);
                    }
                }
            }
        }

        for (var r = firstRow; r <= lastRow; r++)
        {
            var line = rows[r];
            if (change is not null && _doc.IsLineChanged(line))
            {
                Fill(new Rect(view.Left + BarLeft, RowTop(r), 1, height), change);
            }

            var number = (line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Label(number, view.Left + NumbersRight - number.Length * charWidth, RowTop(r), line == caretLine && IsEditable ? Foreground : numbers);

            // The fold button: a chevron, down for an open block and right for a folded one, on a rounded backdrop
            // that eases in under the pointer.
            if (folding.RegionAt(line) is not null)
            {
                var button = FoldButtonRect(view, r);
                var hovered = line == _hoverFoldLine ? _hoverLevel : 0;
                if (hovered > 0 && ThemeBrush("BgHoverBrush") is ISolidColorBrush hover)
                {
                    Shape(SceneKind.RoundFill, button, new Avalonia.Media.Immutable.ImmutableSolidColorBrush(hover.Color, hover.Opacity * hovered));
                }

                var glyph = button.Deflate((button.Width - FoldGlyph) / 2);
                Shape(folding.IsFolded(line) ? SceneKind.ChevronRight : SceneKind.ChevronDown, glyph, hovered > 0.5 ? Foreground : numbers);
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

    private const double DiagnosticFadeMs = 200;

    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0xA8, 0x4E));

    /// <summary>A wave along a rectangle (3 px high), 4 px from crest to crest.</summary>
    internal static SKPath SquigglePath(SKRect rect)
    {
        var path = new SKPath();
        var (top, bottom) = (rect.Top + 0.5f, rect.Bottom - 0.5f);
        path.MoveTo(rect.Left, bottom);
        var up = true;
        for (var x = rect.Left + 2; x <= rect.Right + 0.01f; x += 2)
        {
            path.LineTo(x, up ? top : bottom);
            up = !up;
        }

        return path;
    }

    /// <summary>A row's fold button, in the gutter (which stays put as the text scrolls sideways).</summary>
    private Rect FoldButtonRect(Rect view, int row)
    {
        var x = Math.Round(view.Left + NumbersRight + FoldCenter - FoldButton / 2);
        var y = Math.Round(RowTop(row) + (LineHeight - FoldButton) / 2);
        return new Rect(x, y, FoldButton, FoldButton);
    }

    private static SKColor SkColor(IBrush? brush)
    {
        var color = brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;
        return new SKColor(color.R, color.G, color.B, (byte)(color.A * (brush?.Opacity ?? 1)));
    }

    // The theme's color for each kind of code (CodeKeywordBrush, …), looked up again when the theme changes.
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
        _completion.Close();
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
        _doc.RevealRequested -= OnRevealRequested;
        _doc.RevealRequested += OnRevealRequested;
        _doc.CaretChanged -= OnCaretMoved;
        _doc.CaretChanged += OnCaretMoved;
        _doc.MarksChanged -= InvalidateVisual;
        _doc.MarksChanged += InvalidateVisual;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blink.Stop();
        _doc.Changed -= OnDocumentChanged;
        _doc.Folding.Changed -= OnFoldingChanged;
        _doc.RevealRequested -= OnRevealRequested;
        _doc.CaretChanged -= OnCaretMoved;
        _doc.MarksChanged -= InvalidateVisual;
        _completion.Close();
    }

    // A Go To (or a find's match): the caret's line in the middle of the view, and the focus here to type at it.
    private void OnRevealRequested(bool focus)
    {
        _goalCol = null;
        if (focus)
        {
            Focus();
        }

        RestartBlink();
        Dispatcher.UIThread.Post(() =>
        {
            var rect = CaretRect();
            var half = Math.Max(LineHeight, (_viewport.Height > 0 ? _viewport.Height : Bounds.Height) / 2 - LineHeight);
            this.BringIntoView(rect.Inflate(new Thickness(CharWidth * 4, half, CharWidth * 4, half)));
        }, DispatcherPriority.Loaded);
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

    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor GutterCursor = new(StandardCursorType.Arrow);

    // The fold button under the pointer (its block's first line, -1 for none), and how far its backdrop has eased in.
    private int _hoverFoldLine = -1;
    private double _hoverLevel;
    private double _hoverFrom;
    private double _hoverTarget;
    private long _hoverStart;
    private DispatcherTimer? _hoverTimer;
    private const double HoverMs = 120;

    // The pointer over the text or the gutter: an I-beam over the text, an arrow over the gutter (as in Visual Studio),
    // and the fold button under it lit.
    private void Hover(Point point)
    {
        var view = _viewport.Height > 0 ? _viewport : new Rect(Bounds.Size);
        var inGutter = point.X < view.Left + TextLeft - 2;
        Cursor = inGutter ? GutterCursor : TextCursor;

        var line = -1;
        var rows = Rows;
        var row = (int)Math.Floor((point.Y - Padding.Top) / LineHeight);
        if (inGutter && row >= 0 && row < rows.Count && _doc.Folding.RegionAt(rows[row]) is not null &&
            FoldButtonRect(view, row).Inflate(1).Contains(point))
        {
            line = rows[row];
        }

        if (line == _hoverFoldLine && _hoverTarget == (line >= 0 ? 1 : 0))
        {
            return;
        }

        if (line >= 0)
        {
            // Onto a button: it lights from nothing (another one's backdrop goes at once).
            if (line != _hoverFoldLine)
            {
                _hoverLevel = 0;
            }

            _hoverFoldLine = line;
            FadeHover(1);
        }
        else
        {
            FadeHover(0); // Off it: the backdrop eases out where it is.
        }
    }

    private void FadeHover(double target)
    {
        _hoverFrom = _hoverLevel;
        _hoverTarget = target;
        _hoverStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_hoverTimer is null)
        {
            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _hoverTimer.Tick += (_, _) =>
            {
                var t = Math.Min(1, System.Diagnostics.Stopwatch.GetElapsedTime(_hoverStart).TotalMilliseconds / HoverMs);
                var eased = 1 - Math.Pow(1 - t, 3); // Cubic ease-out.
                _hoverLevel = _hoverFrom + (_hoverTarget - _hoverFrom) * eased;
                if (t >= 1)
                {
                    _hoverTimer!.Stop();
                    if (_hoverTarget == 0)
                    {
                        _hoverFoldLine = -1;
                    }
                }

                InvalidateVisual();
            };
        }

        _hoverTimer.Start();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        // (A screenshot run's window never has the real pointer, which "leaves" it at once: its Quick Info stays.)
        if (Program.Dev.ScreenshotPath is null)
        {
            _pointer = null;
            UpdateLink(false);
            HideQuickInfo();
            _quickAt = null;
        }

        if (_hoverFoldLine >= 0)
        {
            FadeHover(0);
        }
    }

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
        var onBox = _doc.Folding.RegionAt(line) is not null && FoldButtonRect(view, row).Inflate(1).Contains(point);
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

    // ------------------------------------------------------------------ completion

    private readonly CodeCompletion _completion;

    internal CodeDocument Doc => _doc;

    internal IBrush? Theme(string key) => ThemeBrush(key);

    private void OnCaretMoved() => _completion.Update();

    /// <summary>Where a name starts on its line, in this view's coordinates (the completion list hangs below it).</summary>
    internal Rect NameRect(int line, int index)
    {
        var row = _doc.Folding.RowOf(Math.Min(line, _lines.Count - 1));
        return new Rect(TextLeft + Column(new TextPos(line, index)) * CharWidth, RowTop(row), 1, LineHeight);
    }

    /// <summary>
    /// A rect of this view as the window sees it, for a popup to hang from: placed against the view itself (tall as the
    /// whole file, scrolled) a popup lands at the edge of the screen.
    /// </summary>
    internal (Control Target, Rect Rect) WindowAnchor(Rect local) =>
        TopLevel.GetTopLevel(this) is { } top && this.TranslatePoint(local.TopLeft, top) is { } at ? (top, new Rect(at, local.Size)) : (this, local);

    /// <summary>A completion went in: the caret shows where it ended.</summary>
    internal void Committed()
    {
        _goalCol = null;
        RestartBlink();
        RevealCaret();
    }

    /// <summary>In a comment or a string (no completions there).</summary>
    internal bool InCommentOrString(TextPos at) =>
        _doc.Highlighting?.Spans(at.Line) is { } spans &&
        spans.Any(s => s.Start < at.Index && at.Index <= s.Start + s.Length && s.Kind is CodeKind.Comment or CodeKind.String);

    /// <summary>Where names lead and what they are (the group's: the C# language server, or a guess without it).</summary>
    public ICodeNavigator? Navigator { get; set; }

    // A name in the text: where it is, and what comes before its dot ("?" for something that isn't a plain name).
    private readonly record struct Reference(string Name, string? Receiver, int Line, int Start, int End);

    // Keywords that stand for a type: Go To and Quick Info take them to it, as Visual Studio does.
    private static readonly HashSet<string> TypeKeywords =
    [
        "var", "string", "object", "bool", "byte", "sbyte", "char", "short", "ushort", "int", "uint", "long", "ulong",
        "nint", "nuint", "float", "double", "decimal", "dynamic", "this", "base",
    ];

    private Reference? ReferenceAt(TextPos at)
    {
        var text = _doc.Lines[at.Line];
        int start = Math.Min(at.Index, text.Length), end = start;
        while (start > 0 && IsWord(text[start - 1]))
        {
            start--;
        }

        while (end < text.Length && IsWord(text[end]))
        {
            end++;
        }

        if (end == start || char.IsDigit(text[start]))
        {
            return null;
        }

        // Not in a comment or a string, and not a keyword (those that name a type, as string or var, lead to it).
        if (_doc.Highlighting?.Spans(at.Line) is { } spans &&
            spans.Any(s => s.Start <= start && start < s.Start + s.Length &&
                (s.Kind is CodeKind.Comment or CodeKind.String or CodeKind.Control ||
                 s.Kind == CodeKind.Keyword && !TypeKeywords.Contains(text[start..end]))))
        {
            return null;
        }

        string? receiver = null;
        var dot = start - 1;
        if (dot > 0 && text[dot] == '.')
        {
            var j = dot - 1;
            if (j >= 0 && text[j] is '?' or '!')
            {
                j--;
            }

            var k = j;
            while (k >= 0 && IsWord(text[k]))
            {
                k--;
            }

            receiver = k < j ? text[(k + 1)..(j + 1)] : "?";
        }

        return new Reference(text[start..end], receiver, at.Line, start, end);
    }

    // The name right under the pointer (on its letters, not only nearest to it).
    private Reference? ReferenceUnder(Point point)
    {
        var rows = Rows;
        var row = (int)Math.Floor((point.Y - Padding.Top) / LineHeight);
        if (row < 0 || row >= rows.Count || point.X < TextLeft || _doc.Folding.IsFolded(rows[row]) && point.X > TextLeft + Column(new TextPos(rows[row], _doc.Lines[rows[row]].Length)) * CharWidth)
        {
            return null;
        }

        if (ReferenceAt(HitTest(point)) is not { } reference || reference.Line != rows[row])
        {
            return null;
        }

        var from = TextLeft + Column(new TextPos(reference.Line, reference.Start)) * CharWidth;
        var to = TextLeft + Column(new TextPos(reference.Line, reference.End)) * CharWidth;
        return point.X >= from && point.X <= to ? reference : null;
    }

    private static CodeSpot Spot(Reference reference) => new(reference.Name, reference.Receiver, reference.Line, reference.Start);

    private void RequestDefinition(Reference reference) => Navigator?.GoToDefinition(Spot(reference));

    private void RequestImplementation(Reference reference) => Navigator?.GoToImplementation(Spot(reference));

    // ------------------------------------------------------------------ Ctrl+hover and Quick Info

    private static readonly Cursor LinkCursor = new(StandardCursorType.Hand);
    private const double LinkFadeMs = 120;

    // Where the pointer is over the text (null off it).
    private Point? _pointer;

    // With Ctrl held, the name under the pointer that leads somewhere: underlined, and a click goes there. The
    // navigator is asked (it may take a moment); the name under the pointer then is the one asked about.
    private Reference? _link;
    private Reference? _linkAsked;
    private CancellationTokenSource? _linkCts;
    private DateTime _linkUtc;

    private static bool Same(Reference? a, Reference? b) => a is { } x && b is { } y && x.Line == y.Line && x.Start == y.Start;

    private void UpdateLink(bool ctrl)
    {
        var under = ctrl && !_selecting && _pointer is { } point ? ReferenceUnder(point) : null;
        if (under is not null && (Same(under, _link) || Same(under, _linkAsked)))
        {
            return;
        }

        SetLink(null);
        _linkCts?.Cancel();
        _linkAsked = under;
        if (under is { } asked && Navigator is { } navigator)
        {
            var cts = _linkCts = new CancellationTokenSource();
            _ = AskLinkAsync(navigator, asked, cts.Token);
        }
    }

    private async Task AskLinkAsync(ICodeNavigator navigator, Reference asked, CancellationToken cancellationToken)
    {
        bool leads;
        try
        {
            leads = await navigator.LeadsSomewhereAsync(Spot(asked), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested && Same(_linkAsked, asked))
        {
            _linkAsked = null;
            SetLink(leads ? asked : null);
        }
    }

    private void SetLink(Reference? link)
    {
        if (link == _link)
        {
            return;
        }

        _link = link;
        _linkUtc = DateTime.UtcNow;
        if (link is not null)
        {
            Cursor = LinkCursor;
        }
        else if (Cursor == LinkCursor)
        {
            Cursor = TextCursor;
        }

        InvalidateVisual();
    }

    // The pointer resting on a name a moment shows what it is, as Visual Studio's Quick Info.
    private readonly DispatcherTimer _quickTimer = new() { Interval = TimeSpan.FromMilliseconds(225) };
    private Reference? _quickAt;
    private List<Nomad.Core.Lsp.LspDiagnostic> _quickProblems = [];
    private bool _quickShown;
    private CancellationTokenSource? _quickCts;

    private void UpdateQuickInfo()
    {
        var under = !_selecting && _pointer is { } point ? ReferenceUnder(point) : null;
        var problems = !_selecting && _pointer is { } at ? DiagnosticsUnder(at) : [];

        // Off a name but on a squiggle: the problem alone (a place of its own, so moving along it doesn't ask again).
        if (under is null && problems.Count > 0)
        {
            under = new Reference("", null, problems[0].Line, problems[0].Column, problems[0].EndColumn);
        }

        if (Same(under, _quickAt) && (_quickTimer.IsEnabled || _quickShown || _quickCts is { IsCancellationRequested: false }))
        {
            return;
        }

        HideQuickInfo();
        _quickAt = under;
        _quickProblems = problems;
        if (under is not null && (Navigator is not null || problems.Count > 0))
        {
            _quickTimer.Start();
        }
    }

    // The errors and warnings (and suggestions) whose squiggle is under the pointer.
    private List<Nomad.Core.Lsp.LspDiagnostic> DiagnosticsUnder(Point point)
    {
        var rows = Rows;
        var row = (int)Math.Floor((point.Y - Padding.Top) / LineHeight);
        if (_doc.Diagnostics.Count == 0 || row < 0 || row >= rows.Count || point.X < TextLeft)
        {
            return [];
        }

        var line = rows[row];
        var col = (point.X - TextLeft) / CharWidth;
        var found = new List<Nomad.Core.Lsp.LspDiagnostic>();
        foreach (var (d, _) in _doc.Diagnostics)
        {
            if (d.Severity > Nomad.Core.Lsp.LspSeverity.Information || line < d.Line || line > d.EndLine)
            {
                continue;
            }

            var length = _doc.Lines[line].Length;
            var from = line == d.Line ? Math.Min(d.Column, length) : 0;
            var to = line == d.EndLine ? Math.Min(d.EndColumn, length) : length;
            if (to <= from)
            {
                (from, to) = from > 0 ? (from - 1, from) : (from, from + 1);
            }

            if (d.Severity == Nomad.Core.Lsp.LspSeverity.Information)
            {
                to = Math.Min(to, from + 2);
            }

            if (col >= Column(new TextPos(line, from)) - 0.2 && col <= Column(new TextPos(line, to)) + 0.2)
            {
                found.Add(d);
            }
        }

        return [.. found.OrderBy(d => d.Severity)];
    }

    private void HideQuickInfo()
    {
        _quickTimer.Stop();
        _quickCts?.Cancel();
        _quickCts = null;
        if (_quickShown)
        {
            _quickShown = false;
            _quickCard.Opacity = 0;
            DispatcherTimer.RunOnce(() =>
            {
                if (!_quickShown)
                {
                    QuickPopup.IsOpen = false;
                    _quickCard.Child = null;
                }
            }, TimeSpan.FromMilliseconds(130));
        }
    }

    // Quick Info's own popup (not a tooltip: placed under the name, as wide as it needs), fading in and out.
    private readonly Border _quickCard = new()
    {
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 8),
        MaxWidth = 680,
        Opacity = 0,
        IsHitTestVisible = false,
        Transitions =
        [
            new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(130), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
        ],
    };

    private Popup? _quickPopup;

    private Popup QuickPopup
    {
        get
        {
            if (_quickPopup is null)
            {
                _quickPopup = new Popup
                {
                    Child = _quickCard,
                    PlacementTarget = this,
                    Placement = PlacementMode.AnchorAndGravity,
                    PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.BottomLeft,
                    PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
                    IsLightDismissEnabled = false,
                    Focusable = false,
                    IsHitTestVisible = false,
                };
                LogicalChildren.Add(_quickPopup);
            }

            return _quickPopup;
        }
    }

    private async void ShowQuickInfo()
    {
        _quickTimer.Stop();
        if (_quickAt is not { } at)
        {
            return;
        }

        var problems = _quickProblems;
        var cts = _quickCts = new CancellationTokenSource();
        QuickInfoView? info = null;
        if (at.Name.Length > 0 && Navigator is { } navigator)
        {
            try
            {
                info = await navigator.QuickInfoAsync(Spot(at), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (cts.IsCancellationRequested || !Same(_quickAt, at) || info is null && problems.Count == 0)
        {
            return;
        }

        // Under the name (or the squiggle), as Visual Studio's; above it when there's no room below.
        _quickCard.Background = ThemeBrush("BgMenuBrush");
        _quickCard.BorderBrush = ThemeBrush("BorderCardBrush");
        _quickCard.Child = QuickInfoContent(info, problems);
        (QuickPopup.PlacementTarget, QuickPopup.PlacementRect) = WindowAnchor(NameRect(at.Line, at.Start).WithWidth(Math.Max(1, (at.End - at.Start) * CharWidth)));
        QuickPopup.HorizontalOffset = -11;
        QuickPopup.VerticalOffset = 2;
        QuickPopup.IsOpen = true;
        _quickShown = true;
        Dispatcher.UIThread.Post(() => _quickCard.Opacity = _quickShown ? 1 : 0, DispatcherPriority.Loaded);
    }

    // The problems at the place (each with its severity's mark), then what the name is.
    private Control QuickInfoContent(QuickInfoView? info, IReadOnlyList<Nomad.Core.Lsp.LspDiagnostic> problems)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var problem in problems)
        {
            var brush = problem.Severity switch
            {
                Nomad.Core.Lsp.LspSeverity.Error => ThemeBrush("DangerBrush"),
                Nomad.Core.Lsp.LspSeverity.Warning => WarningBrush,
                _ => ThemeBrush("TextMutedBrush"),
            };
            var mark = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = brush, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(4, 5, 4, 0) };
            var message = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };
            if (problem.Code is { Length: > 0 } code)
            {
                message.Inlines!.Add(new Run(code + "  ") { Foreground = ThemeBrush("TextMutedBrush") ?? Foreground });
            }

            message.Inlines!.Add(new Run(problem.Message));
            panel.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { mark, message } });
        }

        if (info is not null)
        {
            if (problems.Count > 0)
            {
                panel.Children.Add(new Border { Height = 1, Background = ThemeBrush("BorderDividerBrush") ?? Brushes.Gray, Margin = new Thickness(0, 2) });
            }

            panel.Children.Add(QuickInfoContent(info));
        }

        return panel;
    }

    // The declaration in the code's colors with its icon, its documentation under it, and how many more there are.
    private Control QuickInfoContent(QuickInfoView info)
    {
        var signature = new TextBlock
        {
            FontFamily = FontFamily,
            FontSize = FontSize,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var text = info.Signature;
        var at = 0;
        var spans = Highlighter.ForFile(info.Path) is { } grammar ? Highlighter.Tokenize(grammar, text, null).Spans : [];
        foreach (var span in spans.Where(s => s.Length > 0 && s.Start >= at).OrderBy(s => s.Start))
        {
            if (span.Start > at)
            {
                signature.Inlines!.Add(new Run(text[at..span.Start]) { Foreground = Foreground });
            }

            var end = Math.Min(text.Length, span.Start + span.Length);
            signature.Inlines!.Add(new Run(text[span.Start..end]) { Foreground = KindBrush(span.Kind) });
            at = end;
        }

        if (at < text.Length)
        {
            signature.Inlines!.Add(new Run(text[at..]) { Foreground = Foreground });
        }

        var icon = new CodeSymbolIcon { Kind = info.Kind, Width = 16, Height = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        TextElement.SetForeground(icon, ThemeBrush("TextSecondaryBrush") ?? Foreground);
        var panel = new StackPanel { Spacing = 6, Children = { new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { icon, signature } } } };
        if (info.Summary is { } summary)
        {
            panel.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, Margin = new Thickness(24, 0, 0, 0) });
        }

        if (info.More > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"+{info.More} more with this name (Ctrl+click lists them)",
                Foreground = ThemeBrush("TextMutedBrush") ?? Foreground,
                Margin = new Thickness(24, 0, 0, 0),
            });
        }

        return panel;
    }

    /// <summary>"Ask about this": the selected lines (or the caret's), first and last (0-based), and their text.</summary>
    public event Action<int, int, string>? AskRequested;

    private void Ask()
    {
        var (from, to) = HasSelection ? Ordered() : (new TextPos(_doc.Caret.Line, 0), new TextPos(_doc.Caret.Line, _doc.Lines[_doc.Caret.Line].Length));

        // A selection ending at a line's start doesn't take that line.
        var last = to.Line > from.Line && to.Index == 0 ? to.Line - 1 : to.Line;
        var text = string.Join("\n", Enumerable.Range(from.Line, last - from.Line + 1).Select(i => _doc.Lines[i]));
        AskRequested?.Invoke(from.Line, last, text);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        HideQuickInfo();
        var props = e.GetCurrentPoint(this).Properties;

        // A right-click with nothing selected puts the caret there first, so the menu is about that place.
        if (props.IsRightButtonPressed && !HasSelection && e.GetPosition(this).X >= TextLeft)
        {
            Focus();
            _doc.Anchor = _doc.Caret = HitTest(e.GetPosition(this));
            RestartBlink();
            InvalidateVisual();
            return;
        }

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

        // Ctrl+click on a name: its declaration (the caret goes there first, as in Visual Studio).
        if (e.ClickCount == 1 && e.KeyModifiers == KeyModifiers.Control && e.GetPosition(this).X >= TextLeft &&
            (_link ?? ReferenceUnder(e.GetPosition(this))) is { } clicked)
        {
            _doc.Seal();
            _doc.Anchor = _doc.Caret = HitTest(e.GetPosition(this));
            RestartBlink();
            RequestDefinition(clicked);
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
            _pointer = e.GetPosition(this);
            Hover(_pointer.Value);
            if (_link is not null)
            {
                Cursor = LinkCursor; // Hover set the text's own.
            }

            UpdateLink(e.KeyModifiers.HasFlag(KeyModifiers.Control));
            UpdateQuickInfo();
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

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            UpdateLink(false);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            // Ctrl down over a name: it turns into a link at once, without waiting for the pointer to move.
            UpdateLink(true);
            return;
        }

        HideQuickInfo();
        if (_completion.HandleKey(e))
        {
            e.Handled = true;
            return;
        }

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
            case Key.F12 when !ctrl && !shift:
                if (ReferenceAt(caret) is { } reference)
                {
                    RequestDefinition(reference);
                }

                break;
            case Key.F12 when ctrl && !shift:
                if (ReferenceAt(caret) is { } implemented)
                {
                    RequestImplementation(implemented);
                }

                break;
            case Key.F12 when shift && !ctrl:
                if (ReferenceAt(caret) is { } used)
                {
                    Navigator?.FindReferences(Spot(used));
                }

                break;
            case Key.F2 when !ctrl && !shift && editable && Navigator is not null:
                if (ReferenceAt(caret) is { } renamed)
                {
                    Navigator.Rename(Spot(renamed));
                }

                break;
            case Key.Space when ctrl && !shift && editable:
                _completion.Start(null, explicitly: true);
                break;
            case Key.I when ctrl && !shift && AskRequested is not null:
                Ask();
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

        _completion.BeforeTyping(e.Text);
        var (from, to) = Ordered();
        _goalCol = null;
        _doc.Replace(from, to, e.Text);
        _completion.AfterTyping(e.Text);
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

    /// <summary>A fill with corners rounded by 3 px.</summary>
    RoundFill,

    /// <summary>A chevron in the rectangle, pointing down (an open block) or right (a folded one).</summary>
    ChevronDown,
    ChevronRight,

    /// <summary>A wavy line along the rectangle's bottom (an error's or warning's).</summary>
    Squiggle,
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
            switch (item.Kind)
            {
                case SceneKind.Fill:
                    c.DrawRect(item.Rect, paint);
                    break;
                case SceneKind.RoundFill:
                    c.DrawRoundRect(item.Rect, 3, 3, paint);
                    break;
                case SceneKind.ChevronDown or SceneKind.ChevronRight:
                    using (var stroke = new SKPaint { Color = item.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round })
                    using (var path = new SKPath())
                    {
                        var (r, down) = (item.Rect, item.Kind == SceneKind.ChevronDown);
                        if (down)
                        {
                            path.MoveTo(r.Left, r.MidY - r.Height / 4);
                            path.LineTo(r.MidX, r.MidY + r.Height / 4);
                            path.LineTo(r.Right, r.MidY - r.Height / 4);
                        }
                        else
                        {
                            path.MoveTo(r.MidX - r.Width / 4, r.Top);
                            path.LineTo(r.MidX + r.Width / 4, r.MidY);
                            path.LineTo(r.MidX - r.Width / 4, r.Bottom);
                        }

                        c.DrawPath(path, stroke);
                    }

                    break;
                case SceneKind.Squiggle:
                    using (var stroke = new SKPaint { Color = item.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f })
                    using (var path = CodeView.SquigglePath(item.Rect))
                    {
                        c.DrawPath(path, stroke);
                    }

                    break;
                default:
                    c.DrawText(item.Text, item.Rect.Left, item.Rect.Top + baseline, SKTextAlign.Left, font, paint);
                    break;
            }
        }

        c.Flush();
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawSurface(target, area.Left, area.Top);
        canvas.Restore();
    }
}

/// <summary>A name in the code for the <see cref="ICodeNavigator"/>: its text, what is before its dot, and where it starts (0-based).</summary>
public readonly record struct CodeSpot(string Name, string? Receiver, int Line, int Column);

/// <summary>What a name is, for its Quick Info: its declaration and documentation, its kind (the icon), the file whose language colors it, and how many more share the name.</summary>
public sealed record QuickInfoView(string Signature, string? Summary, SymbolKind Kind, string Path, int More = 0);

/// <summary>Where the names in a <see cref="CodeView"/> lead and what they are.</summary>
public interface ICodeNavigator
{
    /// <summary>Ctrl over a name: whether a click on it goes anywhere (it is underlined if so).</summary>
    Task<bool> LeadsSomewhereAsync(CodeSpot spot, CancellationToken cancellationToken);

    /// <summary>The pointer rests on a name: what it is (null for nothing to say).</summary>
    Task<QuickInfoView?> QuickInfoAsync(CodeSpot spot, CancellationToken cancellationToken);

    /// <summary>F12 or Ctrl+click: off to its declaration.</summary>
    void GoToDefinition(CodeSpot spot);

    /// <summary>Ctrl+F12: off to what implements it (a list for several).</summary>
    void GoToImplementation(CodeSpot spot);

    /// <summary>Shift+F12: everywhere the name is used.</summary>
    void FindReferences(CodeSpot spot);

    /// <summary>F2: a new name for it, everywhere.</summary>
    void Rename(CodeSpot spot);

    /// <summary>What could be typed at a place (null: no language server to ask).</summary>
    Task<Nomad.Core.Lsp.LspCompletionList?> CompleteAsync(int line, int column, string? trigger, CancellationToken cancellationToken);

    /// <summary>A chosen completion's documentation and the usings it brings.</summary>
    Task<Nomad.Core.Lsp.LspResolvedCompletion?> ResolveCompletionAsync(Nomad.Core.Lsp.LspCompletion item, CancellationToken cancellationToken);
}
