using Avalonia;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Nomad.Core.Text;

namespace Nomad.App.Controls;

/// <summary>
/// A text box presenter that shows Markdown as it is typed, without changing the text: <c>**bold**</c> bold,
/// <c>*italic*</c> italic, <c>`code`</c> as a pill (its backticks hidden; <see cref="MarkdownBackdrop"/> draws the
/// pills and code blocks), fenced blocks in the code font, headings bold, quotes and links in their colours, the
/// syntax around them quiet. List markers stand in from the left as in the transcript: a bullet as a dot, a
/// number, each drawn by a run over the marker's own characters, so the caret steps over them as
/// over text. Every style keeps the line height, so the box never jumps while typing.
/// </summary>
public sealed class MarkdownPresenter : TextPresenter
{
    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<MarkdownPresenter, IBrush?>(nameof(MarkerBrush));

    public static readonly StyledProperty<IBrush?> ListBrushProperty =
        AvaloniaProperty.Register<MarkdownPresenter, IBrush?>(nameof(ListBrush));

    public static readonly StyledProperty<IBrush?> CodeBrushProperty =
        AvaloniaProperty.Register<MarkdownPresenter, IBrush?>(nameof(CodeBrush));

    public static readonly StyledProperty<IBrush?> LinkBrushProperty =
        AvaloniaProperty.Register<MarkdownPresenter, IBrush?>(nameof(LinkBrush));

    public static readonly StyledProperty<IBrush?> QuoteBrushProperty =
        AvaloniaProperty.Register<MarkdownPresenter, IBrush?>(nameof(QuoteBrush));

    public static readonly StyledProperty<FontFamily?> CodeFontFamilyProperty =
        AvaloniaProperty.Register<MarkdownPresenter, FontFamily?>(nameof(CodeFontFamily));

    /// <summary>The code font runs a little smaller, as in the transcript; its lines stay no taller than text.</summary>
    private const double CodeScale = 0.92;

    private IReadOnlyList<MarkdownSpan> _spans = [];
    private readonly HashSet<int> _closingFences = [];

    public IBrush? MarkerBrush
    {
        get => GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public IBrush? ListBrush
    {
        get => GetValue(ListBrushProperty);
        set => SetValue(ListBrushProperty, value);
    }

    public IBrush? CodeBrush
    {
        get => GetValue(CodeBrushProperty);
        set => SetValue(CodeBrushProperty, value);
    }

    public IBrush? LinkBrush
    {
        get => GetValue(LinkBrushProperty);
        set => SetValue(LinkBrushProperty, value);
    }

    public IBrush? QuoteBrush
    {
        get => GetValue(QuoteBrushProperty);
        set => SetValue(QuoteBrushProperty, value);
    }

    public FontFamily? CodeFontFamily
    {
        get => GetValue(CodeFontFamilyProperty);
        set => SetValue(CodeFontFamilyProperty, value);
    }

    /// <summary>The styled stretches of the text, as of the last layout.</summary>
    public IReadOnlyList<MarkdownSpan> Spans => _spans;

    /// <summary>The room above a code block's opening fence and below its closing one, outside its panel (the fences' lines
    /// grow a little more, for room inside it).</summary>
    public double FenceGap => Math.Round(FontSize * 0.7);

    /// <summary>Where the fences that close a code block start, as of the last layout.</summary>
    public IReadOnlySet<int> ClosingFences => _closingFences;

    /// <summary>The text layout is to be made again (the text, the selection, its width or a style changed).</summary>
    public event EventHandler? LayoutChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkerBrushProperty || change.Property == ListBrushProperty || change.Property == CodeBrushProperty
            || change.Property == LinkBrushProperty || change.Property == QuoteBrushProperty || change.Property == CodeFontFamilyProperty)
        {
            InvalidateTextLayout();
        }
    }

    protected override TextLayout CreateTextLayout()
    {
        // The stock layout, for the width and height it was made for (the presenter keeps those to itself).
        var plain = base.CreateTextLayout();
        _spans = [];
        var text = Text;
        if (string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(PreeditText) || PasswordChar != default)
        {
            return plain;
        }

        var maxWidth = plain.MaxWidth;
        var maxHeight = plain.MaxHeight;
        plain.Dispose();

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var defaults = new GenericTextRunProperties(typeface, FontFeatures, FontSize, null, Foreground);
        var paragraph = new GenericTextParagraphProperties(FlowDirection, TextAlignment, true, false, defaults, TextWrapping,
            LineHeight, 0, LetterSpacing);

        var spans = MarkdownSpans.Highlight(text);
        _spans = spans;
        var styled = new List<(int Start, int End, TextRunProperties Properties)>(spans.Count);
        var markers = new SortedList<int, DrawableTextRun>();
        var levels = new Stack<int>();
        var inBlock = false;
        _closingFences.Clear();
        foreach (var span in spans)
        {
            if ((span.Style & (SpanStyle.Bullet | SpanStyle.Number)) != 0)
            {
                // The spaces before a marker nest it: one column in per level, whatever their number.
                var lineStart = span.Start;
                while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t')
                {
                    lineStart--;
                }

                var indent = span.Start - lineStart;
                while (levels.Count > 0 && levels.Peek() > indent)
                {
                    levels.Pop();
                }

                if (levels.Count == 0 || levels.Peek() < indent)
                {
                    levels.Push(indent);
                }

                var number = span.Style.HasFlag(SpanStyle.Number) ? text.Substring(span.Start, span.Length) : null;
                markers.Add(lineStart, new ListMarker(indent + span.Length, levels.Count - 1, number, defaults,
                    ListBrush ?? Foreground ?? Brushes.Gray));
            }
            else if (span.Style == (SpanStyle.Marker | SpanStyle.CodeBlock) && text[span.Start] is '`' or '~')
            {
                // A fence: its first character, drawn by a run with room above (opening) or below (closing),
                // spaces the block off from the text around it.
                var properties = Properties(span.Style);
                var opening = !inBlock;
                inBlock = opening;
                markers.Add(span.Start, new FenceRun(text[span.Start], properties, FenceGap + Math.Round(FontSize * 0.3), opening));
                if (!opening)
                {
                    _closingFences.Add(span.Start);
                }

                styled.Add((span.Start + 1, span.Start + span.Length, properties));
            }
            else
            {
                styled.Add((span.Start, span.Start + span.Length, Properties(span.Style)));
            }
        }

        return new TextLayout(new Source(text, defaults, styled, markers), paragraph, null, maxWidth, maxHeight);
    }

    // The layout itself is made lazily, often while rendering, when nothing may be invalidated: so the
    // backdrop hears of it when the old one is thrown away, or the width changes.
    protected override void InvalidateTextLayout()
    {
        base.InvalidateTextLayout();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return size;
    }

    private GenericTextRunProperties Properties(SpanStyle style)
    {
        var code = (style & (SpanStyle.Code | SpanStyle.CodeBlock)) != 0;
        var typeface = new Typeface(
            code ? CodeFontFamily ?? FontFamily : FontFamily,
            style.HasFlag(SpanStyle.Italic) ? FontStyle.Italic : FontStyle,
            style.HasFlag(SpanStyle.Bold) ? FontWeight.SemiBold : FontWeight,
            FontStretch);

        var foreground = style switch
        {
            // An inline code span's backticks are kept but not shown: they pad its pill.
            _ when style.HasFlag(SpanStyle.Code) && style.HasFlag(SpanStyle.Marker) => Brushes.Transparent,
            _ when style.HasFlag(SpanStyle.Marker) => MarkerBrush,
            _ when style.HasFlag(SpanStyle.Code) => CodeBrush,
            _ when style.HasFlag(SpanStyle.Link) => LinkBrush,
            _ when style.HasFlag(SpanStyle.Quote) => QuoteBrush,
            _ => null,
        } ?? Foreground;

        return new GenericTextRunProperties(typeface, FontFeatures, code ? Math.Round(FontSize * CodeScale) : FontSize,
            style.HasFlag(SpanStyle.Strike) ? TextDecorations.Strikethrough : null, foreground);
    }

    /// <summary>The text in runs of one style each, with list markers and fences as drawn runs.</summary>
    private sealed class Source(
        string text,
        TextRunProperties defaults,
        List<(int Start, int End, TextRunProperties Properties)> styled,
        SortedList<int, DrawableTextRun> markers) : ITextSource
    {
        public TextRun? GetTextRun(int index)
        {
            if (index >= text.Length)
            {
                return null;
            }

            if (markers.TryGetValue(index, out var marker))
            {
                return marker;
            }

            var properties = defaults;
            var end = text.Length;
            foreach (var (start, spanEnd, spanProperties) in styled)
            {
                if (spanEnd <= index)
                {
                    continue;
                }

                if (start <= index)
                {
                    properties = spanProperties;
                    end = spanEnd;
                }
                else
                {
                    end = start;
                }

                break;
            }

            foreach (var start in markers.Keys)
            {
                if (start > index)
                {
                    end = Math.Min(end, start);
                    break;
                }
            }

            return new TextCharacters(text.AsMemory(index, end - index), properties);
        }
    }

    /// <summary>
    /// A list marker (<c>-</c>, <c>*</c>, <c>+</c>, or <c>12.</c>) with the spaces before it, drawn in a column of its
    /// own like the transcript's (one column further in per level): a dot, or the number set to its right edge. It
    /// stands for those characters, so the text is unchanged.
    /// </summary>
    private sealed class ListMarker : DrawableTextRun
    {
        private readonly int _length;
        private readonly TextLayout _shape;
        private readonly IBrush _brush;
        private readonly bool _isBullet;
        private readonly Size _size;

        public ListMarker(int length, int level, string? number, TextRunProperties properties, IBrush brush)
        {
            _length = length;
            _brush = brush;
            _isBullet = number is null;
            Properties = properties;
            _shape = new TextLayout(number ?? "-", properties.Typeface, properties.FontRenderingEmSize, brush);

            // The column: about a bullet's width and a gap in from the left, wider only for long numbers.
            var column = Math.Round(properties.FontRenderingEmSize * 1.5);
            var width = _isBullet ? column : Math.Max(column, Math.Ceiling(_shape.WidthIncludingTrailingWhitespace) + 3);
            _size = new Size((level * column) + width, _shape.Height);
        }

        public override int Length => _length;

        public override TextRunProperties? Properties { get; }

        public override Size Size => _size;

        public override double Baseline => _shape.Baseline;

        public override void Draw(DrawingContext context, Point origin)
        {
            if (_isBullet)
            {
                var radius = Math.Max(2, Properties!.FontRenderingEmSize * 0.14);
                context.DrawEllipse(_brush, null, new Point(origin.X + _size.Width - radius - 6, origin.Y + _size.Height / 2), radius, radius);
                return;
            }

            _shape.Draw(context, new Point(origin.X + _size.Width - _shape.WidthIncludingTrailingWhitespace, origin.Y));
        }
    }

    /// <summary>
    /// A code fence's first character, in a run taller than the line by <c>room</c>: above it for an opening fence,
    /// below for a closing one. The line grows by that much and the code block stands off the text around it.
    /// </summary>
    private sealed class FenceRun : DrawableTextRun
    {
        private readonly TextLayout _shape;
        private readonly double _above;
        private readonly Size _size;

        public FenceRun(char fence, TextRunProperties properties, double room, bool opening)
        {
            Properties = properties;
            _shape = new TextLayout(fence.ToString(), properties.Typeface, properties.FontRenderingEmSize, properties.ForegroundBrush);
            _above = opening ? room : 0;
            _size = new Size(_shape.WidthIncludingTrailingWhitespace, _shape.Height + room);
        }

        public override TextRunProperties? Properties { get; }

        public override Size Size => _size;

        public override double Baseline => _shape.Baseline + _above;

        public override void Draw(DrawingContext context, Point origin) =>
            _shape.Draw(context, new Point(origin.X, origin.Y + _above));
    }
}
