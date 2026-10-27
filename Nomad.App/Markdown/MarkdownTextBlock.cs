using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Nomad.App.Markdown;

/// <summary>
/// A markdown paragraph. Inline code is plain text in it (so a selection can start, end and copy inside it), and
/// this draws the rounded pill behind each code range before the text.
/// </summary>
public sealed class MarkdownTextBlock : SelectableTextBlock
{
    private static readonly ConditionalWeakTable<InlineCollection, List<(int Start, int Length)>> CodeRanges = new();

    /// <summary>The thin space either side of a code pill: drawn as a gap, left out of copied text.</summary>
    public const string PillGap = " ";

    public MarkdownTextBlock()
    {
        CopyingToClipboard += async (_, e) =>
        {
            if (SelectedText is { } text && text.Contains(PillGap, StringComparison.Ordinal) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                e.Handled = true;
                await clipboard.SetTextAsync(text.Replace(PillGap, "", StringComparison.Ordinal));
            }
        };
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public static void AddCodeRange(InlineCollection inlines, int start, int length) =>
        CodeRanges.GetValue(inlines, static _ => []).Add((start, length));

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (Inlines is { } inlines && CodeRanges.TryGetValue(inlines, out var ranges) && ranges.Count > 0)
        {
            var style = MarkdownStyle.Current;
            var background = Brush("BgRaisedBrush", "#1F1F1F");
            var border = new Pen(Brush("InlineCodeBorderBrush", "#393939"), 1);
            var pillHeight = Math.Round(style.CodeSize + 6);
            foreach (var (start, length) in ranges)
            {
                // One rect per line the range covers (a long code span can wrap).
                foreach (var rect in TextLayout.HitTestTextRange(start, length))
                {
                    var y = rect.Y + ((rect.Height - pillHeight) / 2) + 1;
                    var pill = new Rect(rect.X - 3, y, rect.Width + 6, pillHeight).Translate(origin);
                    context.DrawRectangle(background, border, new RoundedRect(pill.Deflate(0.5), 4));
                }
            }
        }

        base.RenderTextLayout(context, origin);
    }

    private static IBrush Brush(string key, string fallback) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));
}
