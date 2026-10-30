using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace Nomad.App.Controls;

/// <summary>Leaves space for one model's token details, so hovering never moves the projects below the card.</summary>
public sealed class UsageHoverSpace : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        double reserve = 0, unfolded = 0;
        if (Child is not { } child)
        {
            return size;
        }

        foreach (var detail in child.GetVisualDescendants().OfType<TextBlock>()
                     .Where(t => t.Classes.Contains("usagedetail") && !string.IsNullOrEmpty(t.Text)))
        {
            var spacing = (detail.Parent as StackPanel)?.Spacing ?? 0;
            var width = Math.Max(1, double.IsFinite(availableSize.Width) ? availableSize.Width : 400);
            using var layout = new TextLayout(detail.Text!, new Typeface(detail.FontFamily, detail.FontStyle, detail.FontWeight),
                detail.FontSize, detail.Foreground, textWrapping: TextWrapping.Wrap, maxWidth: width);
            reserve = Math.Max(reserve, layout.Height + spacing);
            if (detail.IsVisible)
            {
                unfolded += detail.DesiredSize.Height + spacing;
            }
        }

        return new Size(size.Width, Math.Max(0, size.Height - unfolded) + reserve);
    }
}
