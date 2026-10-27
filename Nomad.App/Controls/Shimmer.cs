using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// Text with a soft highlight sweeping across it while <see cref="IsActive"/> ("Thinking", "Running a command"): the
/// working row's sign of life. Runs on render frames only while active and on screen.
/// </summary>
public sealed class Shimmer : TextBlock
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<Shimmer, bool>(nameof(IsActive), true);

    public static readonly StyledProperty<Color> BaseColorProperty =
        AvaloniaProperty.Register<Shimmer, Color>(nameof(BaseColor), Color.Parse("#9C9A92"));

    public static readonly StyledProperty<Color> HighlightColorProperty =
        AvaloniaProperty.Register<Shimmer, Color>(nameof(HighlightColor), Color.Parse("#F5F4EF"));

    /// <summary>One sweep, then a short rest before the next.</summary>
    private const double SweepMs = 1500;
    private const double RestMs = 350;

    private readonly LinearGradientBrush _brush;
    private readonly GradientStop _left;
    private readonly GradientStop _middle;
    private readonly GradientStop _right;
    private bool _running;
    private TimeSpan? _start;

    public Shimmer()
    {
        _left = new GradientStop(BaseColor, 0);
        _middle = new GradientStop(HighlightColor, 0.5);
        _right = new GradientStop(BaseColor, 1);
        _brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(-1, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops = { _left, _middle, _right },
        };
        Foreground = _brush;
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Color BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public Color HighlightColor
    {
        get => GetValue(HighlightColorProperty);
        set => SetValue(HighlightColorProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty || change.Property == IsVisibleProperty)
        {
            Update();
        }
        else if (change.Property == BaseColorProperty)
        {
            _left.Color = _right.Color = BaseColor;
        }
        else if (change.Property == HighlightColorProperty)
        {
            _middle.Color = HighlightColor;
        }
    }

    private void Update()
    {
        var on = IsActive && IsEffectivelyVisible && VisualRoot is not null;
        if (on && !_running && TopLevel.GetTopLevel(this) is { } top)
        {
            _running = true;
            _start = null;
            top.RequestAnimationFrame(Step);
        }
        else if (!on)
        {
            _running = false;
            _middle.Color = BaseColor; // At rest: plain text.
        }
    }

    private void Step(TimeSpan time)
    {
        if (!_running || !IsActive || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top)
        {
            _running = false;
            _middle.Color = BaseColor;
            return;
        }

        _middle.Color = HighlightColor;
        _start ??= time;
        var t = (time - _start.Value).TotalMilliseconds % (SweepMs + RestMs);
        var p = Math.Min(t / SweepMs, 1);

        // The highlight band is as wide as the text and travels from off the left edge to off the right one.
        var eased = p < 0.5 ? 4 * p * p * p : 1 - (Math.Pow(-2 * p + 2, 3) / 2);
        var x = -1 + (2 * eased);
        _brush.StartPoint = new RelativePoint(x, 0, RelativeUnit.Relative);
        _brush.EndPoint = new RelativePoint(x + 1, 0, RelativeUnit.Relative);
        InvalidateVisual();
        top.RequestAnimationFrame(Step);
    }
}
