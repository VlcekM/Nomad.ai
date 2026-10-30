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
/// lines in view, so a long file opens and scrolls at once. Monospace and light (see CodeWeight), tabs to four columns; a drag
/// selects (a double-click a word, a triple-click a line), Ctrl+A selects all and Ctrl+C copies. Unless its
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
    private readonly Dictionary<int, TextLayout> _layouts = [];
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
            _doc = Document ?? CodeDocument.Empty;
            _doc.Changed += OnDocumentChanged;
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
            ClearLayouts();
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

        // A column more than the longest line, for the caret at its end.
        return new Size(p.Left + (_longest + 1) * CharWidth + p.Right, p.Top + _lines.Count * LineHeight + p.Bottom);
    }

    public override void Render(DrawingContext context)
    {
        // Hit-testable everywhere, so a press beside the text still selects.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var p = Padding;
        var height = LineHeight;
        var view = _viewport.Height > 0 ? _viewport : new Rect(Bounds.Size);
        var first = Math.Clamp((int)Math.Floor((view.Top - p.Top) / height), 0, _lines.Count - 1);
        var last = Math.Clamp((int)Math.Ceiling((view.Bottom - p.Top) / height), 0, _lines.Count - 1);
        var charWidth = CharWidth;

        if (HasSelection && SelectionBrush is { } selection)
        {
            var (from, to) = Ordered();
            for (var line = Math.Max(first, from.Line); line <= Math.Min(last, to.Line); line++)
            {
                var start = line == from.Line ? Column(from) : 0;
                var end = line == to.Line ? Column(to) : _lines[line].Length + 1; // A selected line break shows as a space.
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

        if (_caretOn && IsEditable && IsFocused && CaretBrush is { } caret)
        {
            context.FillRectangle(caret, CaretRect());
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

    private Rect CaretRect()
    {
        var p = Padding;
        var caret = _doc.Caret;
        return new Rect(Math.Round(p.Left + Column(caret) * CharWidth), p.Top + caret.Line * LineHeight, 1.5, LineHeight);
    }

    private TextLayout Layout(string text) =>
        new(text, Typeface, FontSize, Foreground, lineHeight: LineHeight);

    private (TextPos From, TextPos To) Ordered() =>
        _doc.Anchor <= _doc.Caret ? (_doc.Anchor, _doc.Caret) : (_doc.Caret, _doc.Anchor);

    // The caret: blinking from on whenever it moves, while the text has the focus.
    private void RestartBlink()
    {
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
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blink.Stop();
        _doc.Changed -= OnDocumentChanged;
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

        if (point.Y > p.Top + _lines.Count * LineHeight)
        {
            return _doc.End;
        }

        var line = Math.Clamp((int)Math.Floor((point.Y - p.Top) / LineHeight), 0, _lines.Count - 1);
        var col = Math.Clamp((int)Math.Round((point.X - p.Left) / CharWidth), 0, _lines[line].Length);
        return new TextPos(line, RawIndex(line, col));
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
        var line = Math.Clamp(p.Line + lines, 0, _lines.Count - 1);
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

        var caret = _doc.Caret;
        var (from, to) = Ordered();
        var editable = IsEditable;
        switch (e.Key)
        {
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
            return p.Index > 0 ? p with { Index = p.Index - 1 } : p.Line > 0 ? new TextPos(p.Line - 1, _doc.Lines[p.Line - 1].Length) : p;
        }

        return p.Index < _doc.Lines[p.Line].Length ? p with { Index = p.Index + 1 } : p.Line + 1 < _lines.Count ? new TextPos(p.Line + 1, 0) : p;
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
