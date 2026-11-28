using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace Nomad.Mobile.Markdown;

/// <summary>
/// Shows Markdown (an agent's reply) as Avalonia text: paragraphs, headings, lists, quotes, code blocks, tables (as plain rows) and
/// inline emphasis, code and links, in the desktop transcript's sizes and colors (its body, headings, lists, framed code blocks and
/// code pills). A tapped web or mail link opens in the phone's browser or mail app. Colors and sizes come from the app's theme
/// resources.
/// </summary>
public sealed class MarkdownBlock : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownBlock, string?>(nameof(Markdown));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseEmphasisExtras().UsePipeTables().UseAutoLinks().UseTaskLists().Build();
    private static readonly FontFamily DefaultMono = new("Cascadia Mono, Consolas, Roboto Mono, Droid Sans Mono, monospace");
    private static readonly TimeSpan StreamThrottle = TimeSpan.FromMilliseconds(50);

    private string? _shown;
    private StackPanel? _panel;
    private readonly List<(int Start, int End, Type Kind, Control? Control, Dictionary<Run, string>? Links)> _renderedBlocks = [];
    private Dictionary<Run, string>? _activeBlockLinks;
    private readonly Dictionary<Run, string> _links = [];

    // Cached theme resources (invalidated on theme change or visual tree attachment).
    private bool _resourcesResolved;
    private double _bodySize = 15;
    private double _lineHeight = 21;
    private double _codeSize = 13;
    private double _tinySize = 11;
    private double _paragraphSpacing = 11;
    private FontFamily _monoFont = DefaultMono;
    private IBrush? _textPrimary;
    private IBrush? _textSecondary;
    private IBrush? _textMuted;
    private IBrush? _borderBrush;
    private IBrush? _bgCode;
    private IBrush? _inlineCode;
    private IBrush? _linkBrush;

    private DispatcherTimer? _throttleTimer;
    private long _lastRenderTicks;

    static MarkdownBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownBlock>((block, _) => block.OnMarkdownChanged());
    }

    /// <summary>The phone switched between light and dark: the colors are looked up again.</summary>
    public MarkdownBlock()
    {
        ActualThemeVariantChanged += (_, _) =>
        {
            _resourcesResolved = false;
            _shown = null;
            RenderNow();
        };
        AddHandler(TappedEvent, OnTapped, handledEventsToo: false);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _resourcesResolved = false;
        if (_shown is null && !string.IsNullOrEmpty(Markdown))
        {
            RenderNow();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _throttleTimer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void OnMarkdownChanged()
    {
        var text = Markdown ?? "";
        if (text == _shown)
        {
            return;
        }

        // Streaming append: coalesce rapid token bursts (~20 fps) so we don't re-parse and re-layout on every token.
        var isStreamingAppend = _shown is { Length: > 0 } prev && text.Length > prev.Length && text.StartsWith(prev, StringComparison.Ordinal);
        if (isStreamingAppend)
        {
            var elapsedMs = (Environment.TickCount64 - _lastRenderTicks);
            if (elapsedMs < StreamThrottle.TotalMilliseconds)
            {
                ScheduleThrottledRender();
                return;
            }
        }

        _throttleTimer?.Stop();
        RenderNow();
    }

    private void ScheduleThrottledRender()
    {
        if (_throttleTimer is null)
        {
            _throttleTimer = new DispatcherTimer(StreamThrottle, DispatcherPriority.Normal, (_, _) =>
            {
                _throttleTimer!.Stop();
                RenderNow();
            });
        }

        if (!_throttleTimer.IsEnabled)
        {
            _throttleTimer.Start();
        }
    }

    private void EnsureResources()
    {
        if (_resourcesResolved)
        {
            return;
        }

        _bodySize = Size("BodyFontSize", 15);
        _lineHeight = Size("BodyLineHeight", 21);
        _codeSize = Size("CodeFontSize", 13);
        _tinySize = Size("UiFontSizeTiny", 11);
        _paragraphSpacing = Math.Round(_bodySize * 0.7);
        _monoFont = this.TryFindResource("MonoFont", out var f) && f is FontFamily font ? font : DefaultMono;
        _textPrimary = Brush("TextPrimaryBrush");
        _textSecondary = Brush("TextSecondaryBrush");
        _textMuted = Brush("TextMutedBrush");
        _borderBrush = Brush("BorderBrush");
        _bgCode = Brush("BgCodeBrush");
        _inlineCode = Brush("InlineCodeBrush");
        _linkBrush = Brush("LinkBrush");
        _resourcesResolved = true;
    }

    private void RenderNow()
    {
        var text = Markdown ?? "";
        if (text == _shown)
        {
            return;
        }

        var prev = _shown;
        _shown = text;
        _lastRenderTicks = Environment.TickCount64;
        EnsureResources();

        if (text.Length == 0)
        {
            _links.Clear();
            _renderedBlocks.Clear();
            _panel = null;
            Content = null;
            return;
        }

        var doc = Markdig.Markdown.Parse(text, Pipeline);
        var isAppend = prev is { Length: > 0 } && text.Length > prev.Length && text.StartsWith(prev, StringComparison.Ordinal);

        // Reuse unchanged prefix blocks during streaming so earlier paragraphs/code blocks are never torn down or re-laid out.
        var keep = 0;
        if (isAppend && _panel is not null && _renderedBlocks.Count > 1 && doc.Count > 1)
        {
            var limit = Math.Min(_renderedBlocks.Count - 1, doc.Count - 1);
            while (keep < limit)
            {
                var nextBlock = doc[keep];
                var old = _renderedBlocks[keep];
                if (nextBlock.Span.Start == old.Start && nextBlock.Span.End == old.End && nextBlock.GetType() == old.Kind)
                {
                    keep++;
                }
                else
                {
                    break;
                }
            }
        }

        if (keep == 0 || _panel is null)
        {
            _links.Clear();
            _renderedBlocks.Clear();
            _panel = new StackPanel { Spacing = _paragraphSpacing };
            for (var i = 0; i < doc.Count; i++)
            {
                AppendTopLevelBlock(doc[i], first: _panel.Children.Count == 0);
            }

            Content = _panel;
        }
        else
        {
            // Remove only the trailing blocks that changed, along with their link entries.
            for (var i = _renderedBlocks.Count - 1; i >= keep; i--)
            {
                var entry = _renderedBlocks[i];
                if (entry.Links is { } oldLinks)
                {
                    foreach (var run in oldLinks.Keys)
                    {
                        _links.Remove(run);
                    }
                }

                if (entry.Control is { } oldControl)
                {
                    _panel.Children.Remove(oldControl);
                }

                _renderedBlocks.RemoveAt(i);
            }

            for (var i = keep; i < doc.Count; i++)
            {
                AppendTopLevelBlock(doc[i], first: _panel.Children.Count == 0);
            }
        }
    }

    private void AppendTopLevelBlock(Block block, bool first)
    {
        _activeBlockLinks = null;
        var control = Block(block, 0, first, quoted: false);
        if (control is not null)
        {
            _panel!.Children.Add(control);
        }

        _renderedBlocks.Add((block.Span.Start, block.Span.End, block.GetType(), control, _activeBlockLinks));
        _activeBlockLinks = null;
    }

    private void RegisterLink(Run run, string url)
    {
        _links[run] = url;
        (_activeBlockLinks ??= [])[run] = url;
    }

    private Control? Block(Block block, int depth, bool first, bool quoted) => block switch
    {
        HeadingBlock heading => Heading(heading, first, quoted),
        ParagraphBlock paragraph => Text(paragraph.Inline, _bodySize, _lineHeight, null, quoted),
        FencedCodeBlock fenced => Code(CodeText(fenced), fenced.Info),
        CodeBlock code => Code(CodeText(code), null),
        ListBlock list => List(list, depth, quoted),
        QuoteBlock quote => Quote(quote, depth),
        Table table => TableBlock(table),
        ThematicBreakBlock => new Border { Height = 1, Background = _borderBrush, Margin = new Thickness(0, 6) },
        ContainerBlock container => Stack(container, depth, quoted),
        _ => null,
    };

    private static string CodeText(LeafBlock block) => string.Join("\n", block.Lines.Lines.Take(block.Lines.Count).Select(l => l.ToString()));

    private Control Heading(HeadingBlock heading, bool first, bool quoted)
    {
        var size = heading.Level switch { 1 => Math.Round(_bodySize * 20 / 15), 2 => Math.Round(_bodySize * 17 / 15), _ => _bodySize };
        var block = Text(heading.Inline, size, Math.Round(size * 1.35), FontWeight.SemiBold, quoted);
        block.Margin = new Thickness(0, first ? 0 : 8, 0, 0);
        return block;
    }

    private StackPanel Stack(ContainerBlock container, int depth, bool quoted)
    {
        var panel = new StackPanel { Spacing = _paragraphSpacing };
        var first = true;
        foreach (var child in container)
        {
            if (Block(child, depth, first, quoted) is { } control)
            {
                panel.Children.Add(control);
                first = false;
            }
        }

        return panel;
    }

    /// <summary>A list as the desktop's: its markers right-aligned in a 31 px column, items close together.</summary>
    private Control List(ListBlock list, int depth, bool quoted)
    {
        var panel = new StackPanel { Spacing = 2 };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        var fg = quoted ? _textSecondary : _textPrimary;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("31,*") };
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}." : "•",
                FontSize = _bodySize,
                LineHeight = _lineHeight,
                Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, list.IsOrdered ? 8 : 17, 0),
            };
            var body = new StackPanel { Spacing = 6 };
            var first = true;
            foreach (var child in item)
            {
                if (Block(child, depth + 1, first, quoted) is { } control)
                {
                    body.Children.Add(control);
                    first = false;
                }
            }

            Grid.SetColumn(body, 1);
            row.Children.Add(marker);
            row.Children.Add(body);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Control Quote(QuoteBlock quote, int depth) => new Border
    {
        BorderBrush = _borderBrush,
        BorderThickness = new Thickness(2, 0, 0, 0),
        Padding = new Thickness(12, 2, 0, 2),
        Child = Stack(quote, depth, quoted: true),
    };

    /// <summary>A code block as the desktop's: framed on the code ground, its language small in the corner; it scrolls sideways.</summary>
    private Control Code(string code, string? language)
    {
        var grid = new Grid();
        grid.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = code.TrimEnd('\n'),
                FontFamily = _monoFont,
                FontSize = _codeSize,
                LineHeight = Math.Round(_codeSize * 1.5),
                Foreground = _textPrimary,
                Margin = new Thickness(12, 11, 12, 11),
            },
        });
        if (!string.IsNullOrWhiteSpace(language))
        {
            grid.Children.Add(new TextBlock
            {
                Text = language.Trim(),
                FontSize = _tinySize,
                Foreground = _textMuted,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 7, 10, 0),
                IsHitTestVisible = false,
            });
        }

        return new Border
        {
            Background = _bgCode,
            BorderBrush = _borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = grid,
        };
    }

    private Control TableBlock(Table table)
    {
        var grid = new Grid();
        var columns = table.OfType<TableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var r = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var c = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var text = new CodePillTextBlock
                {
                    Margin = new Thickness(0, 3, 16, 3),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 260,
                    FontSize = _bodySize,
                    LineHeight = _lineHeight,
                    Foreground = _textPrimary,
                    FontWeight = row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal,
                    Background = Brushes.Transparent,
                };
                var offset = 0;
                foreach (var paragraph in cell.OfType<ParagraphBlock>())
                {
                    if (paragraph.Inline is { } inline)
                    {
                        AddInlines(text.Inlines!, inline, null, ref offset);
                    }
                }

                Grid.SetRow(text, r);
                Grid.SetColumn(text, c++);
                grid.Children.Add(text);
            }

            r++;
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    private TextBlock Text(ContainerInline? inlines, double size, double lineHeight, FontWeight? weight, bool quoted)
    {
        // A background, so a tap anywhere on it (a link) reaches it.
        var block = new CodePillTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            LineHeight = lineHeight,
            Foreground = quoted ? _textSecondary : _textPrimary,
            Background = Brushes.Transparent,
        };
        if (weight is { } w)
        {
            block.FontWeight = w;
        }

        if (inlines is not null)
        {
            var offset = 0;
            AddInlines(block.Inlines!, inlines, null, ref offset);
        }

        return block;
    }

    private void AddInlines(InlineCollection target, ContainerInline container, Func<Run, Run>? style, ref int offset)
    {
        for (MdInline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    var litText = literal.Content.ToString();
                    target.Add(Styled(new Run(litText), style));
                    offset += litText.Length;
                    break;

                case CodeInline code when code.Content.Length > 0:
                    // As the desktop's: a pill on the raised ground, a thin space either side of it.
                    target.Add(new Run(CodePillTextBlock.PillGap));
                    offset += CodePillTextBlock.PillGap.Length;
                    CodePillTextBlock.AddCodeRange(target, offset, code.Content.Length);
                    target.Add(Styled(new Run(code.Content) { FontFamily = _monoFont, Foreground = _inlineCode, FontSize = _codeSize, FontWeight = FontWeight.SemiLight }, style));
                    offset += code.Content.Length;
                    target.Add(new Run(CodePillTextBlock.PillGap));
                    offset += CodePillTextBlock.PillGap.Length;
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    offset += Environment.NewLine.Length;
                    break;

                case EmphasisInline emphasis:
                    var bold = emphasis.DelimiterCount >= 2 && emphasis.DelimiterChar is '*' or '_';
                    var strike = emphasis.DelimiterChar == '~';
                    Func<Run, Run> inner = run =>
                    {
                        run = style?.Invoke(run) ?? run;
                        if (bold) run.FontWeight = FontWeight.SemiBold;
                        else if (strike) run.TextDecorations = TextDecorations.Strikethrough;
                        else run.FontStyle = FontStyle.Italic;
                        return run;
                    };
                    AddInlines(target, emphasis, inner, ref offset);
                    break;

                case LinkInline { IsImage: true } image:
                    var imgText = "[image" + (image.FirstChild is LiteralInline alt ? ": " + alt.Content : "") + "]";
                    target.Add(Styled(new Run(imgText) { Foreground = _textMuted }, style));
                    offset += imgText.Length;
                    break;

                case LinkInline link:
                    Func<Run, Run> linked = run =>
                    {
                        run = style?.Invoke(run) ?? run;
                        run.Foreground = _linkBrush;
                        run.TextDecorations = TextDecorations.Underline;
                        if (link.Url is { } url)
                        {
                            RegisterLink(run, url);
                        }

                        return run;
                    };
                    if (link.FirstChild is null)
                    {
                        var urlText = link.Url ?? "";
                        target.Add(linked(new Run(urlText)));
                        offset += urlText.Length;
                    }
                    else
                    {
                        AddInlines(target, link, linked, ref offset);
                    }

                    break;

                case AutolinkInline auto:
                    var autoRun = Styled(new Run(auto.Url) { Foreground = _linkBrush, TextDecorations = TextDecorations.Underline }, style);
                    RegisterLink(autoRun, auto.IsEmail && !auto.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + auto.Url : auto.Url);
                    target.Add(autoRun);
                    offset += auto.Url.Length;
                    break;

                case HtmlInline html:
                    target.Add(Styled(new Run(html.Tag), style));
                    offset += html.Tag.Length;
                    break;

                case HtmlEntityInline entity:
                    var entText = entity.Transcoded.ToString();
                    target.Add(Styled(new Run(entText), style));
                    offset += entText.Length;
                    break;

                case ContainerInline nested:
                    AddInlines(target, nested, style, ref offset);
                    break;
            }
        }
    }

    /// <summary>A tap on a link's text opens it: web and mail links only (a file path on the PC means nothing on the phone).</summary>
    private async void OnTapped(object? sender, TappedEventArgs e)
    {
        if (_links.Count == 0 || (e.Source as Visual)?.FindAncestorOfType<TextBlock>(includeSelf: true) is not { Inlines: { } inlines } block
            || UrlAt(block, inlines, e.GetPosition(block)) is not { } url
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")
            || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return;
        }

        e.Handled = true;
        if (!await OpenAsync(this, uri.AbsoluteUri)
            && this.GetVisualAncestors().Select(v => (v as StyledElement)?.DataContext).OfType<Nomad.Mobile.ViewModels.ChatViewModel>().FirstOrDefault() is { } chat)
        {
            chat.ShowProblem("Couldn't open " + url);
        }
    }

    /// <summary>Opens a web or mail link in the phone's own app for it; false when it can't (no launcher, or the phone refused).</summary>
    public static async Task<bool> OpenAsync(Visual from, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")
            || TopLevel.GetTopLevel(from)?.Launcher is not { } launcher)
        {
            return false;
        }

        try
        {
            return await launcher.LaunchUriAsync(uri);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The web and mail links a message's text holds, in order, each once: what tapping them would open.</summary>
    public static IReadOnlyList<string> LinksIn(string markdown)
    {
        var found = new List<string>();
        foreach (var inline in Markdig.Markdown.Parse(markdown, Pipeline).Descendants<MdInline>())
        {
            var url = inline switch
            {
                LinkInline { IsImage: false } link => link.Url,
                AutolinkInline auto => auto.IsEmail && !auto.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + auto.Url : auto.Url,
                _ => null,
            };
            if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" && !found.Contains(url))
            {
                found.Add(url);
            }
        }

        return found;
    }

    /// <summary>The link under a point of a text block: the character there, found among its runs in order.</summary>
    private string? UrlAt(TextBlock block, InlineCollection inlines, Point point)
    {
        var hit = block.TextLayout.HitTestPoint(point - new Point(block.Padding.Left, block.Padding.Top));
        if (!hit.IsInside)
        {
            return null;
        }

        // The character under the finger: TextPosition moves on to the next one over a character's right half.
        var position = hit.CharacterHit.FirstCharacterIndex;
        var start = 0;
        foreach (var inline in inlines)
        {
            var length = LengthOf(inline);
            if (position >= start && position < start + length)
            {
                return inline is Run r ? _links.GetValueOrDefault(r) : null;
            }

            start += length;
        }

        return null;
    }

    private static Run Styled(Run run, Func<Run, Run>? style) => style?.Invoke(run) ?? run;

    /// <summary>How many characters an inline takes in its text block's text.</summary>
    private static int LengthOf(Avalonia.Controls.Documents.Inline inline) => inline switch
    {
        Run run => run.Text?.Length ?? 0,
        LineBreak => Environment.NewLine.Length,
        _ => 0,
    };

    private double Size(string key, double fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is double size ? size
        : Application.Current?.TryFindResource(key, out value) == true && value is double appSize ? appSize
        : fallback;

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush
        : Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out value) == true && value is IBrush appBrush ? appBrush
        : null;
}
