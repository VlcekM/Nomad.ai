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
/// inline emphasis, code and links. A tapped web or mail link opens in the phone's browser or mail app. Colors come from the
/// app's theme resources.
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
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in Markdig.Markdown.Parse(text, Pipeline))
        {
            if (Block(block, 0) is { } control)
            {
                panel.Children.Add(control);
            }
        }

        Content = panel;
    }

    private Control? Block(Block block, int depth) => block switch
    {
        HeadingBlock heading => Text(heading.Inline, heading.Level <= 2 ? 1.25 : 1.1, FontWeight.SemiBold),
        ParagraphBlock paragraph => Text(paragraph.Inline),
        FencedCodeBlock or CodeBlock => Code(string.Join("\n", ((LeafBlock)block).Lines.Lines.Take(((LeafBlock)block).Lines.Count).Select(l => l.ToString()))),
        ListBlock list => List(list, depth),
        QuoteBlock quote => Quote(quote, depth),
        Table table => TableBlock(table),
        ThematicBreakBlock => new Border { Height = 1, Background = Brush("BorderBrush"), Margin = new Thickness(0, 4) },
        ContainerBlock container => Stack(container, depth),
        _ => null,
    };

    private StackPanel Stack(ContainerBlock container, int depth)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var child in container)
        {
            if (Block(child, depth) is { } control)
            {
                panel.Children.Add(control);
            }
        }

        return panel;
    }

    private Control List(ListBlock list, int depth)
    {
        var panel = new StackPanel { Spacing = 4 };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var marker = list.IsOrdered ? $"{number++}." : depth % 2 == 0 ? "•" : "◦";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var bullet = new TextBlock { Text = marker, Margin = new Thickness(0, 0, 8, 0), Foreground = Brush("TextSecondaryBrush"), MinWidth = list.IsOrdered ? 20 : 10 };
            var body = Stack(item, depth + 1);
            Grid.SetColumn(body, 1);
            row.Children.Add(bullet);
            row.Children.Add(body);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Control Quote(QuoteBlock quote, int depth) => new Border
    {
        BorderBrush = Brush("BorderInputBrush"),
        BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(10, 0, 0, 0),
        Child = Stack(quote, depth),
    };

    private Control Code(string code) => new Border
    {
        Background = Brush("BgCodeBrush"),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 8),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = code.TrimEnd('\n'),
                FontFamily = MonoFont,
                FontSize = 12.5,
                Foreground = Brush("TextPrimaryBrush"),
            },
        },
    };

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
                var text = new TextBlock { Margin = new Thickness(0, 2, 14, 2), TextWrapping = TextWrapping.Wrap, MaxWidth = 260, FontWeight = row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal, Background = Brushes.Transparent };
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

    private TextBlock Text(ContainerInline? inlines, double scale = 1, FontWeight? weight = null)
    {
        // A background, so a tap anywhere on it (a link) reaches it.
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextPrimaryBrush"), Background = Brushes.Transparent };
        if (scale != 1)
        {
            block.FontSize = 15 * scale;
        }

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

                case CodeInline code:
                    target.Add(Styled(new Run(code.Content) { FontFamily = MonoFont, Foreground = Brush("InlineCodeBrush"), FontSize = 13.5 }, style));
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
        await launcher.LaunchUriAsync(uri);
    }

    /// <summary>The link under a point of a text block: the character there, found among its runs in order.</summary>
    private string? UrlAt(TextBlock block, InlineCollection inlines, Point point)
    {
        var hit = block.TextLayout.HitTestPoint(point - new Point(block.Padding.Left, block.Padding.Top));
        if (!hit.IsInside)
        {
            return null;
        }

        var start = 0;
        foreach (var inline in inlines)
        {
            var length = inline switch
            {
                Run run => run.Text?.Length ?? 0,
                LineBreak => Environment.NewLine.Length,
                _ => 0,
            };
            if (hit.TextPosition >= start && hit.TextPosition < start + length)
            {
                return inline is Run r ? _links.GetValueOrDefault(r) : null;
            }

            start += length;
        }

        return null;
    }

    private static Run Styled(Run run, Func<Run, Run>? style) => style?.Invoke(run) ?? run;

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Roboto Mono, Droid Sans Mono, monospace");

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush
        : Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out value) == true && value is IBrush appBrush ? appBrush
        : null;
}
