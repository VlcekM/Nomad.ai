using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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

    private string? _shown;

    // The runs of each link and where it goes, for a tap on one.
    private readonly Dictionary<Run, string> _links = [];

    static MarkdownBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownBlock>((block, _) => block.Render());
    }

    /// <summary>The phone switched between light and dark: the colors are looked up again.</summary>
    public MarkdownBlock()
    {
        ActualThemeVariantChanged += (_, _) =>
        {
            _shown = null;
            Render();
        };
        AddHandler(TappedEvent, OnTapped, handledEventsToo: false);
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Render()
    {
        var text = Markdown ?? "";
        if (text == _shown)
        {
            return;
        }

        _shown = text;
        _links.Clear();
        var panel = new StackPanel { Spacing = ParagraphSpacing };
        var first = true;
        foreach (var block in Markdig.Markdown.Parse(text, Pipeline))
        {
            if (Block(block, 0, first, quoted: false) is { } control)
            {
                panel.Children.Add(control);
                first = false;
            }
        }

        Content = panel;
    }

    // The desktop's transcript sizes (Nomad.App's MarkdownStyle at its default): body 15 on 21, code 13, headings 20, 17, 15.
    private double BodySize => Size("BodyFontSize", 15);

    private double LineHeight => Size("BodyLineHeight", 21);

    private double CodeSize => Size("CodeFontSize", 13);

    private double ParagraphSpacing => Math.Round(BodySize * 0.7);

    private Control? Block(Block block, int depth, bool first, bool quoted) => block switch
    {
        HeadingBlock heading => Heading(heading, first, quoted),
        ParagraphBlock paragraph => Text(paragraph.Inline, BodySize, LineHeight, null, quoted),
        FencedCodeBlock fenced => Code(CodeText(fenced), fenced.Info),
        CodeBlock code => Code(CodeText(code), null),
        ListBlock list => List(list, depth, quoted),
        QuoteBlock quote => Quote(quote, depth),
        Table table => TableBlock(table),
        ThematicBreakBlock => new Border { Height = 1, Background = Brush("BorderBrush"), Margin = new Thickness(0, 6) },
        ContainerBlock container => Stack(container, depth, quoted),
        _ => null,
    };

    private static string CodeText(LeafBlock block) => string.Join("\n", block.Lines.Lines.Take(block.Lines.Count).Select(l => l.ToString()));

    private Control Heading(HeadingBlock heading, bool first, bool quoted)
    {
        var size = heading.Level switch { 1 => Math.Round(BodySize * 20 / 15), 2 => Math.Round(BodySize * 17 / 15), _ => BodySize };
        var block = Text(heading.Inline, size, Math.Round(size * 1.35), FontWeight.SemiBold, quoted);
        block.Margin = new Thickness(0, first ? 0 : 8, 0, 0);
        return block;
    }

    private StackPanel Stack(ContainerBlock container, int depth, bool quoted)
    {
        var panel = new StackPanel { Spacing = ParagraphSpacing };
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
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("31,*") };
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}." : "•",
                FontSize = BodySize,
                LineHeight = LineHeight,
                Foreground = Brush(quoted ? "TextSecondaryBrush" : "TextPrimaryBrush"),
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
        BorderBrush = Brush("BorderBrush"),
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
                FontFamily = MonoFont,
                FontSize = CodeSize,
                LineHeight = Math.Round(CodeSize * 1.5),
                Foreground = Brush("TextPrimaryBrush"),
                Margin = new Thickness(12, 11, 12, 11),
            },
        });
        if (!string.IsNullOrWhiteSpace(language))
        {
            grid.Children.Add(new TextBlock
            {
                Text = language.Trim(),
                FontSize = Size("UiFontSizeTiny", 11),
                Foreground = Brush("TextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 7, 10, 0),
                IsHitTestVisible = false,
            });
        }

        return new Border
        {
            Background = Brush("BgCodeBrush"),
            BorderBrush = Brush("BorderBrush"),
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
                    FontSize = BodySize,
                    LineHeight = LineHeight,
                    Foreground = Brush("TextPrimaryBrush"),
                    FontWeight = row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal,
                    Background = Brushes.Transparent,
                };
                foreach (var paragraph in cell.OfType<ParagraphBlock>())
                {
                    if (paragraph.Inline is { } inline)
                    {
                        AddInlines(text.Inlines!, inline, null);
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
            Foreground = Brush(quoted ? "TextSecondaryBrush" : "TextPrimaryBrush"),
            Background = Brushes.Transparent,
        };
        if (weight is { } w)
        {
            block.FontWeight = w;
        }

        if (inlines is not null)
        {
            AddInlines(block.Inlines!, inlines, null);
        }

        return block;
    }

    private void AddInlines(InlineCollection target, ContainerInline container, Func<Run, Run>? style)
    {
        for (MdInline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(Styled(new Run(literal.Content.ToString()), style));
                    break;

                case CodeInline code when code.Content.Length > 0:
                    // As the desktop's: a pill on the raised ground, a thin space either side of it.
                    target.Add(new Run(CodePillTextBlock.PillGap));
                    CodePillTextBlock.AddCodeRange(target, LengthOf(target), code.Content.Length);
                    target.Add(Styled(new Run(code.Content) { FontFamily = MonoFont, Foreground = Brush("InlineCodeBrush"), FontSize = CodeSize, FontWeight = FontWeight.SemiLight }, style));
                    target.Add(new Run(CodePillTextBlock.PillGap));
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
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
                    AddInlines(target, emphasis, inner);
                    break;

                case LinkInline { IsImage: true } image:
                    target.Add(Styled(new Run("[image" + (image.FirstChild is LiteralInline alt ? ": " + alt.Content : "") + "]") { Foreground = Brush("TextMutedBrush") }, style));
                    break;

                case LinkInline link:
                    Func<Run, Run> linked = run =>
                    {
                        run = style?.Invoke(run) ?? run;
                        run.Foreground = Brush("LinkBrush");
                        run.TextDecorations = TextDecorations.Underline;
                        if (link.Url is { } url)
                        {
                            _links[run] = url;
                        }

                        return run;
                    };
                    if (link.FirstChild is null)
                    {
                        target.Add(linked(new Run(link.Url ?? "")));
                    }
                    else
                    {
                        AddInlines(target, link, linked);
                    }

                    break;

                case AutolinkInline auto:
                    var autoRun = Styled(new Run(auto.Url) { Foreground = Brush("LinkBrush"), TextDecorations = TextDecorations.Underline }, style);
                    _links[autoRun] = auto.IsEmail && !auto.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + auto.Url : auto.Url;
                    target.Add(autoRun);
                    break;

                case HtmlInline html:
                    target.Add(Styled(new Run(html.Tag), style));
                    break;

                case HtmlEntityInline entity:
                    target.Add(Styled(new Run(entity.Transcoded.ToString()), style));
                    break;

                case ContainerInline nested:
                    AddInlines(target, nested, style);
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

    /// <summary>How many characters an inline (or all of them) takes in its text block's text.</summary>
    private static int LengthOf(Avalonia.Controls.Documents.Inline inline) => inline switch
    {
        Run run => run.Text?.Length ?? 0,
        LineBreak => Environment.NewLine.Length,
        _ => 0,
    };

    private static int LengthOf(InlineCollection inlines) => inlines.Sum(LengthOf);

    private double Size(string key, double fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is double size ? size
        : Application.Current?.TryFindResource(key, out value) == true && value is double appSize ? appSize
        : fallback;

    private FontFamily MonoFont => this.TryFindResource("MonoFont", out var value) && value is FontFamily font ? font : DefaultMono;

    private static readonly FontFamily DefaultMono = new("Cascadia Mono, Consolas, Roboto Mono, Droid Sans Mono, monospace");

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush
        : Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out value) == true && value is IBrush appBrush ? appBrush
        : null;
}
