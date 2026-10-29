using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Nomad.Core.Agents;

namespace Nomad.App.Controls;

/// <summary>
/// The logo of the agent a session runs on (<see cref="AgentBackends"/>), from the 16×16 geometries in
/// Theme/Icons.axaml: Claude's spark in its orange, OpenCode's framed block in the text colours (so it follows the theme).
/// </summary>
public sealed class AgentLogo : Control
{
    public static readonly StyledProperty<string?> BackendProperty =
        AvaloniaProperty.Register<AgentLogo, string?>(nameof(Backend));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<AgentLogo, double>(nameof(Size), 12);

    static AgentLogo()
    {
        AffectsRender<AgentLogo>(BackendProperty);
        AffectsMeasure<AgentLogo>(SizeProperty);
        VerticalAlignmentProperty.OverrideDefaultValue<AgentLogo>(Avalonia.Layout.VerticalAlignment.Center);
        HorizontalAlignmentProperty.OverrideDefaultValue<AgentLogo>(Avalonia.Layout.HorizontalAlignment.Center);
    }

    public AgentLogo()
    {
        // Its brushes are looked up while drawing: a theme or colour change redraws it.
        ResourcesChanged += (_, _) => InvalidateVisual();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public string? Backend
    {
        get => GetValue(BackendProperty);
        set => SetValue(BackendProperty, value);
    }

    /// <summary>Rendered size in pixels.</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var scale = Size / 16.0;
        var offset = new Point((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            if (AgentBackends.Normalize(Backend) == AgentBackends.OpenCode)
            {
                Fill(context, "IconOpenCodeBlock", "TextMutedBrush");
                Fill(context, "IconOpenCodeFrame", "TextPrimaryBrush");
            }
            else
            {
                Fill(context, "IconClaude", "ClaudeBrandBrush");
            }
        }
    }

    private void Fill(DrawingContext context, string geometry, string brush)
    {
        if (this.TryFindResource(geometry, out var g) && g is Geometry data
            && this.TryFindResource(brush, ActualThemeVariant, out var b) && b is IBrush fill)
        {
            context.DrawGeometry(fill, null, data);
        }
    }
}
