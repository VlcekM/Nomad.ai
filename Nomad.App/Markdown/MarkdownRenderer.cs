using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace Nomad.App.Markdown;

/// <summary>
/// Turns a Markdig syntax tree into Avalonia controls: paragraphs and headings become
/// <see cref="SelectableTextBlock"/>s with inlines, lists become two-column grids, fenced code
/// becomes a scrollable mono box, tables become simple grids.
/// </summary>
public static class MarkdownRenderer
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    private static IBrush Brush(string key, Color fallback) =>
        Application.Current?.TryGetResource(key, null, out var value) == true && value is IBrush b ? b : new SolidColorBrush(fallback);

    private static IBrush TextPrimary => Brush("TextPrimaryBrush", Color.Parse("#EEEDE8"));
    private static IBrush TextSecondary => Brush("TextSecondaryBrush", Color.Parse("#A8A6A0"));
    private static IBrush TextMuted => Brush("TextMutedBrush", Color.Parse("#8A8883"));
    private static IBrush BorderLine => Brush("BorderBrush", Color.Parse("#363636"));
    private static IBrush SubtleLine => Brush("BorderSubtleBrush", Color.Parse("#10FFFFFF"));
    private static IBrush CodeBg => Brush("BgCodeBrush", Color.Parse("#1A1A19"));
    private static IBrush RaisedBg => Brush("BgRaisedBrush", Color.Parse("#1F1F1F"));
    private static IBrush InlineCode => Brush("InlineCodeBrush", Color.Parse("#DE8170"));
    private static IBrush Link => Brush("LinkBrush", Color.Parse("#6FA2EA"));

    /// <summary>Renders one top-level block. <paramref name="isFirst"/> drops the heading top margin.</summary>
    public static Control RenderBlock(Block block, bool isFirst, bool quoted = false)
    {
        var style = MarkdownStyle.Current;
        switch (block)
        {
            case HeadingBlock heading:
            {
                var size = heading.Level switch { 1 => style.H1, 2 => style.H2, _ => style.H3 };
                var tb = NewTextBlock(size, Math.Round(size * 1.35), quoted ? TextSecondary : TextPrimary);
                tb.FontWeight = FontWeight.SemiBold;
                tb.Margin = new Thickness(0, isFirst ? 0 : 8, 0, 0);
                AddInlines(tb, heading.Inline, default);
                return tb;
            }

            case ParagraphBlock paragraph:
            {
                var tb = NewTextBlock(style.BodySize, style.LineHeight, quoted ? TextSecondary : TextPrimary);
                AddInlines(tb, paragraph.Inline, default);
                return tb;
            }

            case ListBlock list:
                return RenderList(list, quoted);

            case FencedCodeBlock fenced:
                return RenderCode(CodeText(fenced), fenced.Info);

            case CodeBlock code:
                return RenderCode(CodeText(code), null);

            case QuoteBlock quote:
            {
                var panel = new StackPanel { Spacing = style.ParagraphSpacing };
                var first = true;
                foreach (var child in quote)
                {
                    panel.Children.Add(RenderBlock(child, first, quoted: true));
                    first = false;
                }

                return new Border
                {
                    BorderBrush = BorderLine,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(12, 2, 0, 2),
                    Child = panel,
                };
            }

            case ThematicBreakBlock:
                return new Border { Height = 1, Background = BorderLine, Margin = new Thickness(0, 6) };

            case Table table:
                return RenderTable(table);

            case HtmlBlock html:
            {
                var tb = NewTextBlock(style.BodySize, style.LineHeight, TextSecondary);
                tb.Text = html.Lines.ToString().TrimEnd();
                return tb;
            }

            case LinkReferenceDefinitionGroup:
                return new Panel();

            case ContainerBlock container:
            {
                var panel = new StackPanel { Spacing = style.ParagraphSpacing };
                var first = true;
                foreach (var child in container)
                {
                    panel.Children.Add(RenderBlock(child, first, quoted));
                    first = false;
                }

                return panel;
            }

            case LeafBlock leaf:
            {
                var tb = NewTextBlock(style.BodySize, style.LineHeight, TextPrimary);
                tb.Text = leaf.Lines.ToString();
                return tb;
            }

            default:
                return new Panel();
        }
    }

    private static MarkdownTextBlock NewTextBlock(double size, double lineHeight, IBrush foreground)
    {
        var tb = new MarkdownTextBlock
        {
            FontSize = size,
            LineHeight = lineHeight,
            Foreground = foreground,
            FontFamily = MarkdownStyle.Current.BodyFont,
            TextWrapping = TextWrapping.Wrap,
        };
        LinkSupport.Attach(tb);
        return tb;
    }

    // ------------------------------------------------------------------ inlines

    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Strike);

    private static void AddInlines(SelectableTextBlock target, ContainerInline? container, InlineStyle style)
    {
        if (container is null)
        {
            return;
        }

        var inlines = target.Inlines ??= new InlineCollection();
        var links = LinkSupport.GetLinks(target);
        AddInlines(inlines, links, container, style);
    }

    private static void AddInlines(InlineCollection target, List<LinkRange> links, ContainerInline container, InlineStyle style)
    {
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Append(target, literal.Content.ToString(), style);
                    break;
                case EmphasisInline emphasis:
                    var s = style;
                    if (emphasis.DelimiterChar is '*' or '_')
                    {
                        s = emphasis.DelimiterCount >= 2 ? s with { Bold = true } : s with { Italic = true };
                    }
                    else if (emphasis.DelimiterChar == '~')
                    {
                        s = s with { Strike = true };
                    }

                    AddInlines(target, links, emphasis, s);
                    break;
                case CodeInline code:
                    AppendCode(target, links, code.Content);
                    break;
                case LinkInline { IsImage: true } image:
                    Append(target, "[" + PlainText(image) + "]", style);
                    break;
                case LinkInline link:
                    AppendLink(target, links, PlainText(link), link.Url, style);
                    break;
                case AutolinkInline auto:
                    AppendLink(target, links, auto.Url, auto.IsEmail ? "mailto:" + auto.Url : auto.Url, style);
                    break;
                case LineBreakInline:
                    // Soft breaks too: Claude's replies use single newlines as line breaks (lists of
                    // numbers, addresses, short poems), and the original app shows them that way.
                    Append(target, "\n", style);
                    break;
                case HtmlEntityInline entity:
                    Append(target, entity.Transcoded.ToString(), style);
                    break;
                case HtmlInline html:
                    Append(target, html.Tag, style);
                    break;
                case TaskList task:
                    Append(target, task.Checked ? "☑ " : "☐ ", style);
                    break;
                case ContainerInline nested:
                    AddInlines(target, links, nested, style);
                    break;
                default:
                    Append(target, inline.ToString() ?? "", style);
                    break;
            }
        }
    }

    private static void Append(InlineCollection target, string text, InlineStyle style)
    {
        if (text.Length == 0)
        {
            return;
        }

        target.Add(MakeRun(text, style));
        LinkSupport.Advance(target, text.Length);
    }

    private static Run MakeRun(string text, InlineStyle style)
    {
        var run = new Run(text);
        if (style.Bold)
        {
            run.FontWeight = FontWeight.SemiBold;
        }

        if (style.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (style.Strike)
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }

        return run;
    }

    private static void AppendLink(InlineCollection target, List<LinkRange> links, string text, string? url, InlineStyle style)
    {
        if (text.Length == 0)
        {
            text = url ?? "";
        }

        var start = LinkSupport.Position(target);
        var run = MakeRun(text, style);
        run.Foreground = Link;
        run.TextDecorations = TextDecorations.Underline;
        target.Add(run);
        LinkSupport.Advance(target, text.Length);
        if (!string.IsNullOrEmpty(url))
        {
            links.Add(new LinkRange(start, start + text.Length, url));
        }
    }

    // Real text, so its letters select like the rest; MarkdownTextBlock draws the pill behind it. A URL or a file
    // path in it can be opened (LinkSupport: a click on a URL, Ctrl+click or right-click on a path).
    private static void AppendCode(InlineCollection target, List<LinkRange> links, string code)
    {
        if (code.Length == 0)
        {
            return;
        }

        // A thin space each side keeps the pill off its neighbours (and comes out again on copy).
        target.Add(new Run(MarkdownTextBlock.PillGap));
        LinkSupport.Advance(target, 1);
        var style = MarkdownStyle.Current;
        var start = LinkSupport.Position(target);
        var run = new Run(code)
        {
            FontFamily = style.CodeFont,
            FontSize = style.CodeSize,
            Foreground = InlineCode,
        };
        target.Add(run);
        LinkSupport.Advance(target, code.Length);
        if (LinkSupport.CodeTarget(code) is { } kind)
        {
            links.Add(new LinkRange(start, start + code.Length, kind == CodeLinkKind.Url ? code.Trim() : LinkSupport.PathPrefix + code.Trim()));
            if (kind is CodeLinkKind.Url or CodeLinkKind.AbsolutePath)
            {
                run.TextDecorations = TextDecorations.Underline;
            }
        }

        MarkdownTextBlock.AddCodeRange(target, start, code.Length);
        target.Add(new Run(MarkdownTextBlock.PillGap));
        LinkSupport.Advance(target, 1);
    }

    private static string PlainText(ContainerInline container)
    {
        var sb = new StringBuilder();
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case ContainerInline nested:
                    sb.Append(PlainText(nested));
                    break;
            }
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ lists

    private static Control RenderList(ListBlock list, bool quoted)
    {
        var style = MarkdownStyle.Current;
        var panel = new StackPanel { Spacing = 2 };
        var number = 1;
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start))
        {
            number = start;
        }

        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("31,*") };
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number}." : "•",
                FontSize = style.BodySize,
                LineHeight = style.LineHeight,
                Foreground = quoted ? TextSecondary : TextPrimary,
                FontFamily = style.BodyFont,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, list.IsOrdered ? 8 : 17, 0),
            };
            grid.Children.Add(marker);

            var content = new StackPanel { Spacing = 6 };
            var first = true;
            foreach (var block in item)
            {
                content.Children.Add(RenderBlock(block, first, quoted));
                first = false;
            }

            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            panel.Children.Add(grid);
            number++;
        }

        return panel;
    }

    // ------------------------------------------------------------------ code

    private static string CodeText(LeafBlock block)
    {
        var lines = block.Lines;
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(lines.Lines[i].Slice.ToString());
        }

        return sb.ToString();
    }

    public static Control RenderCode(string code, string? language)
    {
        var style = MarkdownStyle.Current;
        var text = new SelectableTextBlock
        {
            Text = code,
            FontFamily = style.CodeFont,
            FontSize = style.CodeSize,
            LineHeight = Math.Round(style.CodeSize * 1.5),
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(12, 11, 12, 11),
        };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = text,
        };

        var grid = new Grid();
        grid.Children.Add(scroll);

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "" : language.Trim(),
            [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("UiFontSizeTiny"),
            Foreground = TextMuted,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 10, 0),
            IsHitTestVisible = false,
        };
        grid.Children.Add(label);

        var copy = new Button
        {
            Content = new TextBlock { Text = "Copy", [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("UiFontSizeSmall") },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 6, 0),
            Padding = new Thickness(8, 2),
            Opacity = 0,
        };
        if (Application.Current?.TryGetResource("SecondaryButton", null, out var theme) == true && theme is Avalonia.Styling.ControlTheme ct)
        {
            copy.Theme = ct;
            copy.Height = 24;
        }

        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(copy)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(code);
                if (copy.Content is TextBlock t)
                {
                    t.Text = "Copied";
                }
            }
        };
        grid.Children.Add(copy);
        grid.PointerEntered += (_, _) =>
        {
            copy.Opacity = 1;
            label.Opacity = 0;
        };
        grid.PointerExited += (_, _) =>
        {
            copy.Opacity = 0;
            label.Opacity = 1;
            if (copy.Content is TextBlock t)
            {
                t.Text = "Copy";
            }
        };

        return new Border
        {
            Background = CodeBg,
            BorderBrush = BorderLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = grid,
        };
    }

    // ------------------------------------------------------------------ tables

    private static Control RenderTable(Table table)
    {
        var style = MarkdownStyle.Current;
        var columns = table.ColumnDefinitions.Count;
        if (columns == 0)
        {
            columns = table.OfType<TableRow>().Select(r => r.Count).DefaultIfEmpty(1).Max();
        }

        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var rowIndex = 0;
        foreach (var rowBlock in table)
        {
            if (rowBlock is not TableRow row)
            {
                continue;
            }

            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var col = 0;
            foreach (var cellBlock in row)
            {
                if (cellBlock is not TableCell cell || col >= columns)
                {
                    continue;
                }

                var tb = NewTextBlock(style.BodySize - 1, style.LineHeight - 2, TextPrimary);
                tb.MaxWidth = 420;
                if (row.IsHeader)
                {
                    tb.FontWeight = FontWeight.SemiBold;
                }

                foreach (var para in cell.OfType<ParagraphBlock>())
                {
                    AddInlines(tb, para.Inline, default);
                }

                var border = new Border
                {
                    Padding = new Thickness(10, 6),
                    BorderBrush = row.IsHeader ? BorderLine : SubtleLine,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Background = row.IsHeader ? RaisedBg : Brushes.Transparent,
                    Child = tb,
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, col);
                grid.Children.Add(border);
                col++;
            }

            rowIndex++;
        }

        return new Border
        {
            BorderBrush = BorderLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = grid,
            },
        };
    }
}

/// <summary>A clickable range of a text block's text.</summary>
public sealed record LinkRange(int Start, int End, string Url);

/// <summary>What an inline code span may point at.</summary>
public enum CodeLinkKind
{
    Url,
    AbsolutePath,
    RelativePath,
}

/// <summary>
/// Links are ordinary underlined runs; clicks are mapped back to them by hit-testing the text
/// layout, so long links wrap like the rest of the paragraph.
/// </summary>
public static partial class LinkSupport
{
    private static readonly AttachedProperty<List<LinkRange>?> LinksProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, SelectableTextBlock, List<LinkRange>?>("Links");

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InlineCollection, StrongBox> Positions = new();

    private sealed class StrongBox
    {
        public int Value;
    }

    public static List<LinkRange> GetLinks(SelectableTextBlock block)
    {
        var links = block.GetValue(LinksProperty);
        if (links is null)
        {
            links = [];
            block.SetValue(LinksProperty, links);
        }

        return links;
    }

    public static int Position(InlineCollection inlines) => Positions.GetValue(inlines, static _ => new StrongBox()).Value;

    public static void Advance(InlineCollection inlines, int length) => Positions.GetValue(inlines, static _ => new StrongBox()).Value += length;

    /// <summary>Marks a link that is a file path from inline code (opened in Explorer, not the browser).</summary>
    public const string PathPrefix = "path:";

    public static void Attach(SelectableTextBlock block)
    {
        Point? pressedAt = null;
        block.PointerPressed += (_, e) => pressedAt = e.GetPosition(block);
        block.PointerReleased += (_, e) =>
        {
            var at = e.GetPosition(block);
            if (pressedAt is not { } p || Math.Abs(p.X - at.X) > 3 || Math.Abs(p.Y - at.Y) > 3)
            {
                return;
            }

            if (Hit(block, at) is not { } link)
            {
                return;
            }

            // A path opens with Ctrl+click: a plain click stays free for putting the caret in or selecting it.
            if (!link.Url.StartsWith(PathPrefix, StringComparison.Ordinal))
            {
                Open(link.Url);
                e.Handled = true;
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && ResolvePath(block, link.Url) is { } path)
            {
                ShowInExplorer(path);
                e.Handled = true;
            }
        };
        block.PointerMoved += (_, e) =>
        {
            var links = block.GetValue(LinksProperty);
            if (links is null || links.Count == 0)
            {
                return;
            }

            var link = Hit(block, e.GetPosition(block));
            var path = link?.Url.StartsWith(PathPrefix, StringComparison.Ordinal) == true ? ResolvePath(block, link.Url) : null;
            var hand = link is not null && (path is null ? !link.Url.StartsWith(PathPrefix, StringComparison.Ordinal) : e.KeyModifiers.HasFlag(KeyModifiers.Control));
            block.Cursor = hand ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Ibeam);
            var tip = path is not null ? "Ctrl+click to show in Explorer · right-click for more" : null;
            if (!Equals(ToolTip.GetTip(block), tip))
            {
                ToolTip.SetTip(block, tip);
            }
        };

        // Right-click on a path or URL in inline code: open it, show it in Explorer, copy it.
        block.ContextRequested += (_, e) =>
        {
            if (!e.TryGetPosition(block, out var at) || Hit(block, at) is not { } link)
            {
                return;
            }

            var menu = new ContextMenu();
            if (link.Url.StartsWith(PathPrefix, StringComparison.Ordinal))
            {
                if (ResolvePath(block, link.Url) is not { } path)
                {
                    return;
                }

                menu.Items.Add(Item("Show in Explorer", () => ShowInExplorer(path)));
                menu.Items.Add(Item(Directory.Exists(path) ? "Open folder" : "Open", () => OpenWithShell(path)));
                menu.Items.Add(Item("Copy path", () => _ = TopLevel.GetTopLevel(block)?.Clipboard?.SetTextAsync(path)));
            }
            else
            {
                menu.Items.Add(Item("Open link", () => Open(link.Url)));
                menu.Items.Add(Item("Copy link", () => _ = TopLevel.GetTopLevel(block)?.Clipboard?.SetTextAsync(link.Url)));
            }

            menu.Open(block);
            e.Handled = true;
        };
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Whether inline code looks like something to open: a web URL, an absolute path (<c>R:\x</c>, <c>\\server\x</c>,
    /// <c>~/x</c>, <c>%APPDATA%\x</c>), or a relative path with a folder or an extension (<c>src/App.cs</c>). Paths
    /// are only opened once they turn out to exist.
    /// </summary>
    public static CodeLinkKind? CodeTarget(string code)
    {
        var text = code.Trim();
        if (text.Length < 3 || text.Length > 400 || text.Contains('\n'))
        {
            return null;
        }

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return text.Contains(' ') ? null : CodeLinkKind.Url;
        }

        if ((char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/') || text.StartsWith(@"\\", StringComparison.Ordinal)
            || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith(@"~\", StringComparison.Ordinal) || text.StartsWith('%'))
        {
            return CodeLinkKind.AbsolutePath;
        }

        // Commands and code ("git push", "a = b", "Foo()") are not paths.
        if (text.IndexOfAny([' ', '(', ')', '=', '<', '>', '|', '"', '*', '?', ';']) >= 0)
        {
            return null;
        }

        return text.Contains('/') || text.Contains('\\') || Path.HasExtension(text) ? CodeLinkKind.RelativePath : null;
    }

    /// <summary>
    /// The existing file or folder a path link names: environment variables and <c>~</c> expanded, a trailing
    /// <c>:line</c> or <c>:line:column</c> dropped, a relative path taken from the session's folder. Null otherwise.
    /// </summary>
    private static string? ResolvePath(Control block, string url)
    {
        var text = Environment.ExpandEnvironmentVariables(url[PathPrefix.Length..].Trim().TrimEnd('.', ',', ';'));
        if (text.StartsWith('~'))
        {
            text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];
        }

        text = LineSuffix().Replace(text, "").Replace('/', '\\');
        try
        {
            if (!Path.IsPathRooted(text))
            {
                var folder = (block.FindAncestorOfType<Nomad.App.Views.SessionView>()?.DataContext as Nomad.App.ViewModels.SessionViewModel)?.Folder;
                if (folder is null)
                {
                    return null;
                }

                text = Path.Combine(folder, text);
            }

            var full = Path.GetFullPath(text);
            return File.Exists(full) || Directory.Exists(full) ? full.TrimEnd('\\') is { Length: > 2 } trimmed ? trimmed : full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@":\d+(:\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex LineSuffix();

    /// <summary>Opens Explorer on the item's folder with the item selected.</summary>
    public static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception)
        {
            // No Explorer (unusual Windows setups): nothing to do.
        }
    }

    private static void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // No app for this file type.
        }
    }

    private static LinkRange? Hit(SelectableTextBlock block, Point point)
    {
        var links = block.GetValue(LinksProperty);
        if (links is null || links.Count == 0)
        {
            return null;
        }

        var local = point - new Point(block.Padding.Left, block.Padding.Top);
        var hit = block.TextLayout.HitTestPoint(local);
        if (!hit.IsInside)
        {
            return null;
        }

        var position = hit.TextPosition;
        return links.FirstOrDefault(l => position >= l.Start && position < l.End);
    }

    /// <summary>Opens web and mail links in the default browser; other schemes are ignored.</summary>
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser registered: nothing sensible to do.
        }
    }
}
