using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Nomad.Mobile.Markdown;

/// <summary>
/// A Markdown paragraph that draws the desktop's rounded pill behind each inline code range before the text (Nomad.App's
/// MarkdownTextBlock, on a plain <see cref="TextBlock"/>: transcript text isn't selectable on the phone, so a drag on it scrolls).
/// </summary>
public sealed class CodePillTextBlock : TextBlock
{
    private static readonly ConditionalWeakTable<InlineCollection, List<(int Start, int Length)>> CodeRanges = new();

    /// <summary>The thin space either side of a code pill, which keeps it off its neighbors.</summary>
    public const string PillGap = " ";

    private IBrush? _background;
    private Pen? _border;
    private double _pillHeight;
    private bool _resourcesResolved;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public CodePillTextBlock()
    {
        ActualThemeVariantChanged += (_, _) => _resourcesResolved = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _resourcesResolved = false;
    }

    public static void AddCodeRange(InlineCollection inlines, int start, int length) =>
        CodeRanges.GetValue(inlines, static _ => []).Add((start, length));

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (Inlines is { } inlines && CodeRanges.TryGetValue(inlines, out var ranges) && ranges.Count > 0)
        {
            if (!_resourcesResolved)
            {
                _background = Brush("BgRaisedBrush", "#1B1B1D");
                _border = new Pen(Brush("InlineCodeBorderBrush", "#2E2E30"), 1);
                var codeSize = this.TryFindResource("CodeFontSize", out var size) && size is double d ? d : 13;
                _pillHeight = Math.Round(codeSize + 6);
                _resourcesResolved = true;
            }

            foreach (var (start, length) in ranges)
            {
                // One rect per line the range covers (a long code span can wrap).
                foreach (var rect in TextLayout.HitTestTextRange(start, length))
                {
                    var y = rect.Y + ((rect.Height - _pillHeight) / 2) + 1;
                    var pill = new Rect(rect.X - 3, y, rect.Width + 6, _pillHeight).Translate(origin);
                    context.DrawRectangle(_background, _border, new RoundedRect(pill.Deflate(0.5), 4));
                }
            }
        }

        base.RenderTextLayout(context, origin);
    }

    private IBrush Brush(string key, string fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));
}
