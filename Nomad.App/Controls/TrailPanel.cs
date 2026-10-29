using Avalonia;
using Avalonia.Controls;

namespace Nomad.App.Controls;

/// <summary>
/// Two children on one line: the second (a logo) sits right after the first's text rather than at the panel's far edge.
/// The first takes the width the second leaves, so a text too long for the line fades out (<see cref="EdgeFade"/>)
/// before the second child.
/// </summary>
public sealed class TrailPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return default;
        }

        var head = Children[0];
        var tailWidth = 0.0;
        var tailHeight = 0.0;
        if (Children.Count > 1)
        {
            Children[1].Measure(availableSize);
            tailWidth = Children[1].DesiredSize.Width;
            tailHeight = Children[1].DesiredSize.Height;
        }

        head.Measure(new Size(Math.Max(0, availableSize.Width - tailWidth), availableSize.Height));
        var headSize = head.DesiredSize;
        return new Size(headSize.Width + tailWidth, Math.Max(headSize.Height, tailHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }

        // The head gets at most what the line has left after the tail: measured against a wider line (a layout pass
        // with more room, a row narrowing), it would overrun the row and be cut off by it rather than fade out.
        var head = Children[0];
        var tailWidth = Children.Count > 1 ? Children[1].DesiredSize.Width : 0;
        var headWidth = Math.Max(0, Math.Min(head.DesiredSize.Width, finalSize.Width - tailWidth));
        head.Arrange(new Rect(0, 0, headWidth, finalSize.Height));
        if (Children.Count > 1)
        {
            Children[1].Arrange(new Rect(headWidth, 0, tailWidth, finalSize.Height));
        }

        return finalSize;
    }
}
