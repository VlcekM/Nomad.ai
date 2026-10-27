using Avalonia;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Nomad.Core.Text;

namespace Nomad.App.Controls;

/// <summary>
/// A text box presenter that shows Markdown as it is typed, without changing the text: <c>**bold**</c> bold,
/// <c>*italic*</c> italic, <c>`code`</c> and fenced blocks in the code font, headings bold, quotes and links in
/// their colours, the syntax around them quiet, and list markers in the accent (bullets drawn as dots by
/// <see cref="MarkdownBullets"/>). Every style keeps the line height, so the box never jumps while typing.
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

    private readonly List<int> _bullets = [];

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

    /// <summary>Where the bullets (<c>-</c>, <c>*</c>, <c>+</c> of list items) are in the text, as of the last layout.</summary>
    public IReadOnlyList<int> Bullets => _bullets;

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
        _bullets.Clear();
        var text = Text;
        if (string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(PreeditText) || PasswordChar != default)
        {
            return plain;
        }

        var maxWidth = plain.MaxWidth;
        var maxHeight = plain.MaxHeight;
        plain.Dispose();

        var overrides = new List<ValueSpan<TextRunProperties>>();
        foreach (var span in MarkdownSpans.Highlight(text))
        {
            overrides.Add(new ValueSpan<TextRunProperties>(span.Start, span.Length, Properties(span.Style)));
            if (span.Style.HasFlag(MarkdownStyle.Bullet))
            {
                _bullets.Add(span.Start);
            }
        }

        var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontFeatures, FontSize,
            Foreground, TextAlignment, TextWrapping, null, null, FlowDirection, maxWidth, maxHeight, LineHeight, LetterSpacing, 0,
            overrides);
        return layout;
    }

    // The layout itself is made lazily, often while rendering, when nothing may be invalidated: so the
    // bullets hear of it when the old one is thrown away, or the width changes.
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

    private GenericTextRunProperties Properties(MarkdownStyle style)
    {
        var code = (style & (MarkdownStyle.Code | MarkdownStyle.CodeBlock)) != 0;
        var typeface = new Typeface(
            code ? CodeFontFamily ?? FontFamily : FontFamily,
            style.HasFlag(MarkdownStyle.Italic) ? FontStyle.Italic : FontStyle,
            style.HasFlag(MarkdownStyle.Bold) ? FontWeight.SemiBold : FontWeight,
            FontStretch);

        var foreground = style switch
        {
            _ when style.HasFlag(MarkdownStyle.Bullet) => Brushes.Transparent,
            _ when style.HasFlag(MarkdownStyle.Number) => ListBrush,
            _ when style.HasFlag(MarkdownStyle.Marker) => MarkerBrush,
            _ when style.HasFlag(MarkdownStyle.Code) => CodeBrush,
            _ when style.HasFlag(MarkdownStyle.Link) => LinkBrush,
            _ when style.HasFlag(MarkdownStyle.Quote) => QuoteBrush,
            _ => null,
        } ?? Foreground;

        return new GenericTextRunProperties(typeface, FontFeatures, code ? Math.Round(FontSize * CodeScale) : FontSize,
            style.HasFlag(MarkdownStyle.Strike) ? TextDecorations.Strikethrough : null, foreground);
    }
}
