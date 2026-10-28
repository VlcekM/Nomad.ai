using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nomad.App.Controls;

/// <summary>
/// A color picker (the theme editor's): a saturation and brightness square over a hue strip and an opacity strip,
/// each with a ring at the color. Press or drag in any of them; the rings follow the pointer. A color set from
/// outside (a hex value typed, a reset) glides the rings to it.
/// </summary>
public sealed class ColorPad : Control
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorPad, Color>(nameof(Color), Colors.White, defaultBindingMode: BindingMode.TwoWay);

    private const double StripHeight = 12;
    private const double Gap = 10;
    private const double RingRadius = 7;
    private static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(160);

    private static readonly IBrush HueBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(255, 0, 0), 0),
            new GradientStop(Color.FromRgb(255, 255, 0), 1 / 6.0),
            new GradientStop(Color.FromRgb(0, 255, 0), 2 / 6.0),
            new GradientStop(Color.FromRgb(0, 255, 255), 3 / 6.0),
            new GradientStop(Color.FromRgb(0, 0, 255), 4 / 6.0),
            new GradientStop(Color.FromRgb(255, 0, 255), 5 / 6.0),
            new GradientStop(Color.FromRgb(255, 0, 0), 1),
        },
    };

    private static readonly IBrush WhiteToClear = Gradient(Colors.White, Color.FromArgb(0, 255, 255, 255), horizontal: true);
    private static readonly IBrush ClearToBlack = Gradient(Color.FromArgb(0, 0, 0, 0), Colors.Black, horizontal: false);
    private static readonly IBrush CheckLight = new SolidColorBrush(Color.FromRgb(204, 204, 204));
    private static readonly IBrush CheckDark = new SolidColorBrush(Color.FromRgb(140, 140, 140));
    private static readonly IPen RingPen = new Pen(Brushes.White, 2);
    private static readonly IPen RingShadow = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1);

    // The color as hue (0–360), saturation, brightness and opacity (0–1): the rings' places. Kept apart from Color,
    // so a gray or black keeps the hue it was dragged at.
    private (double H, double S, double V, double A) _hsva;
    private (double H, double S, double V, double A) _shown;
    private (double H, double S, double V, double A) _glideFrom;
    private DateTime _glideStart;
    private DispatcherTimer? _timer;
    private Part _dragging;
    private bool _setting;

    private enum Part { None, Square, Hue, Alpha }

    static ColorPad()
    {
        AffectsRender<ColorPad>(ColorProperty);
        CursorProperty.OverrideDefaultValue<ColorPad>(new Cursor(StandardCursorType.Hand));
    }

    public ColorPad()
    {
        _hsva = _shown = ToHsva(Color, 0);
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Min(availableSize.Width, 220);
        return new Size(width, width * 0.66 + 2 * (Gap + StripHeight));
    }

    private Rect Square => new(0, 0, Bounds.Width, Bounds.Height - 2 * (Gap + StripHeight));

    private Rect HueStrip => new(0, Square.Bottom + Gap, Bounds.Width, StripHeight);

    private Rect AlphaStrip => new(0, HueStrip.Bottom + Gap, Bounds.Width, StripHeight);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ColorProperty || _setting)
        {
            return;
        }

        _hsva = ToHsva(Color, _hsva.H);
        if (!IsLoaded)
        {
            _shown = _hsva;
            return;
        }

        _glideFrom = _shown;
        _glideStart = DateTime.UtcNow;
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Glide());
        _timer.Start();
    }

    private void Glide()
    {
        var t = Math.Clamp((DateTime.UtcNow - _glideStart) / GlideTime, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        double Lerp(double from, double to) => from + (to - from) * eased;
        _shown = (Lerp(_glideFrom.H, _hsva.H), Lerp(_glideFrom.S, _hsva.S), Lerp(_glideFrom.V, _hsva.V), Lerp(_glideFrom.A, _hsva.A));
        if (t >= 1)
        {
            _timer?.Stop();
        }

        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var p = e.GetPosition(this);
        _dragging = p.Y <= Square.Bottom + Gap / 2 ? Part.Square : p.Y <= HueStrip.Bottom + Gap / 2 ? Part.Hue : Part.Alpha;
        _timer?.Stop();
        e.Pointer.Capture(this);
        SetFromPointer(p);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging != Part.None)
        {
            SetFromPointer(e.GetPosition(this));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging != Part.None)
        {
            _dragging = Part.None;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = Part.None;
    }

    private void SetFromPointer(Point p)
    {
        _hsva = _dragging switch
        {
            Part.Square => _hsva with
            {
                S = Math.Clamp(p.X / Math.Max(1, Square.Width), 0, 1),
                V = 1 - Math.Clamp(p.Y / Math.Max(1, Square.Height), 0, 1),
            },
            Part.Hue => _hsva with { H = FractionOf(HueStrip, p.X) * 360 },
            _ => _hsva with { A = FractionOf(AlphaStrip, p.X) },
        };
        _shown = _hsva;
        _setting = true;
        Color = FromHsva(_hsva);
        _setting = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var (h, s, v, a) = _shown;
        var square = Square;
        var hue = HueStrip;
        var alpha = AlphaStrip;
        var opaque = FromHsva((h, s, v, 1));

        // Saturation runs left to right over the pure hue, brightness top to bottom.
        context.DrawRectangle(new SolidColorBrush(FromHsva((h, 1, 1, 1))), null, square, 6, 6);
        context.DrawRectangle(WhiteToClear, null, square, 6, 6);
        context.DrawRectangle(ClearToBlack, null, square, 6, 6);

        context.DrawRectangle(HueBrush, null, hue, StripHeight / 2, StripHeight / 2);

        // Opacity over a checkerboard, so what is see-through shows.
        using (context.PushClip(new RoundedRect(alpha, StripHeight / 2)))
        {
            const double cell = StripHeight / 2;
            for (var cx = 0; cx * cell < alpha.Width; cx++)
            {
                for (var cy = 0; cy < 2; cy++)
                {
                    context.FillRectangle((cx + cy) % 2 == 0 ? CheckLight : CheckDark, new Rect(alpha.X + cx * cell, alpha.Y + cy * cell, cell, cell));
                }
            }

            context.FillRectangle(Gradient(Color.FromArgb(0, opaque.R, opaque.G, opaque.B), opaque, horizontal: true), alpha);
        }

        DrawRing(context, new Point(square.X + s * square.Width, square.Y + (1 - v) * square.Height), opaque);
        DrawRing(context, new Point(Along(hue, h / 360), hue.Center.Y), FromHsva((h, 1, 1, 1)));
        DrawRing(context, new Point(Along(alpha, a), alpha.Center.Y), FromHsva((h, s, v, a)));
    }

    // A ring's center along a strip, kept inside its rounded ends, and back.
    private static double Along(Rect strip, double fraction) =>
        strip.X + strip.Height / 2 + Math.Clamp(fraction, 0, 1) * (strip.Width - strip.Height);

    private static double FractionOf(Rect strip, double x) =>
        Math.Clamp((x - strip.X - strip.Height / 2) / Math.Max(1, strip.Width - strip.Height), 0, 1);

    private static void DrawRing(DrawingContext context, Point center, Color fill)
    {
        context.DrawEllipse(null, RingShadow, center, RingRadius + 1, RingRadius + 1);
        context.DrawEllipse(new SolidColorBrush(fill), RingPen, center, RingRadius, RingRadius);
    }

    private static LinearGradientBrush Gradient(Color from, Color to, bool horizontal) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(horizontal ? 1 : 0, horizontal ? 0 : 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
    };

    private static (double H, double S, double V, double A) ToHsva(Color c, double hue)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta > 0)
        {
            hue = max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
            if (hue < 0)
            {
                hue += 360;
            }
        }

        return (hue, max == 0 ? 0 : delta / max, max, c.A / 255.0);
    }

    private static Color FromHsva((double H, double S, double V, double A) hsva)
    {
        var (h, s, v, a) = hsva;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
        return Color.FromArgb(Byte(a), Byte(r + m), Byte(g + m), Byte(b + m));
    }
}
