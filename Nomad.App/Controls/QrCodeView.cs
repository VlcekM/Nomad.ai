using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Nomad.Core.Text;

namespace Nomad.App.Controls;

/// <summary>
/// A QR code drawn black on white with its 4-module quiet zone, whatever the theme, so a camera reads it. Each module
/// is a whole number of device pixels (the side comes out at or just under <see cref="Side"/>), and edges are drawn
/// aliased, so the code stays crisp at any display scaling.
/// </summary>
public sealed class QrCodeView : Control
{
    private const int QuietZone = 4;

    public static readonly StyledProperty<QrCode?> CodeProperty =
        AvaloniaProperty.Register<QrCodeView, QrCode?>(nameof(Code));

    /// <summary>The side to aim for, quiet zone included, px.</summary>
    public static readonly StyledProperty<double> SideProperty =
        AvaloniaProperty.Register<QrCodeView, double>(nameof(Side), 200);

    static QrCodeView()
    {
        AffectsMeasure<QrCodeView>(CodeProperty, SideProperty);
        AffectsRender<QrCodeView>(CodeProperty);
    }

    public QrCodeView() => RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

    public QrCode? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public double Side
    {
        get => GetValue(SideProperty);
        set => SetValue(SideProperty, value);
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>A module's size in px: a whole number of device pixels, at least one.</summary>
    private double ModuleSize(QrCode code)
    {
        var scaling = Scaling;
        var modules = code.Size + QuietZone * 2;
        return Math.Max(1, Math.Floor(Side * scaling / modules)) / scaling;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InvalidateMeasure(); // The scaling is known now.
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Code is not { } code)
        {
            return default;
        }

        var side = ModuleSize(code) * (code.Size + QuietZone * 2);
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        if (Code is not { } code)
        {
            return;
        }

        var m = ModuleSize(code);
        context.FillRectangle(Brushes.White, new Rect(0, 0, m * (code.Size + QuietZone * 2), m * (code.Size + QuietZone * 2)));
        for (var r = 0; r < code.Size; r++)
        {
            // One rectangle per run of dark modules in a row.
            for (var c = 0; c < code.Size; c++)
            {
                if (!code[r, c])
                {
                    continue;
                }

                var start = c;
                while (c + 1 < code.Size && code[r, c + 1])
                {
                    c++;
                }

                context.FillRectangle(Brushes.Black, new Rect((start + QuietZone) * m, (r + QuietZone) * m, (c - start + 1) * m, m));
            }
        }
    }
}
