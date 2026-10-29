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

        var head = Children[0];
        head.Arrange(new Rect(0, 0, head.DesiredSize.Width, finalSize.Height));
        if (Children.Count > 1)
        {
            var tail = Children[1];
            tail.Arrange(new Rect(head.DesiredSize.Width, 0, tail.DesiredSize.Width, finalSize.Height));
        }

        return finalSize;
    }
}
