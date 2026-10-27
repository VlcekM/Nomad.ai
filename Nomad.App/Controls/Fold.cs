using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// <c>c:Fold.IsOpen</c>: instead of popping in and out like <c>IsVisible</c>, the control unfolds its height from 0
/// and fades in, sliding down a little, and folds away the same way (200 ms, cubic ease-out). A change mid-flight
/// retargets from where it is. Runs on render frames, only while moving.
/// </summary>
public static class Fold
{
    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsOpen", typeof(Fold));

    private const double DurationMs = 200;
    private const double Slide = 4;

    private sealed class Run
    {
        public bool Open;
        public double FromHeight;
        public double ToHeight;
        public double FromOpacity;
        public TimeSpan? Start;
    }

    private static readonly Dictionary<Control, Run> Runs = [];

    static Fold()
    {
        IsOpenProperty.Changed.AddClassHandler<Control>((control, e) => Begin(control, e.GetNewValue<bool>()));
    }

    public static bool GetIsOpen(Control control) => control.GetValue(IsOpenProperty);

    public static void SetIsOpen(Control control, bool value) => control.SetValue(IsOpenProperty, value);

    private static void Begin(Control control, bool open)
    {
        // Not laid out yet (a template being built, a list loading): take the state at once.
        if (TopLevel.GetTopLevel(control) is not { } top || (!control.IsVisible && !open) || (control.Parent as Control)?.Bounds.Width is null or <= 0)
        {
            Runs.Remove(control);
            control.IsVisible = open;
            return;
        }

        var running = Runs.ContainsKey(control);
        var from = control.IsVisible ? control.Bounds.Height : 0;
        var opacity = control.IsVisible ? control.Opacity : 0;
        control.ClipToBounds = true;
        control.IsVisible = true;

        double to = 0;
        if (open)
        {
            // Its natural height at the width it will get.
            var width = control.Bounds.Width > 0 ? control.Bounds.Width : ((Control)control.Parent!).Bounds.Width - control.Margin.Left - control.Margin.Right;
            control.Height = double.NaN;
            control.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
            to = Math.Max(0, control.DesiredSize.Height - control.Margin.Top - control.Margin.Bottom);
        }

        control.Height = from;
        control.Opacity = opacity;
        Runs[control] = new Run { Open = open, FromHeight = from, ToHeight = to, FromOpacity = opacity };
        if (!running)
        {
            top.RequestAnimationFrame(t => Step(control, t));
        }
    }

    private static void Step(Control control, TimeSpan time)
    {
        if (!Runs.TryGetValue(control, out var run))
        {
            return;
        }

        run.Start ??= time;
        var progress = Math.Clamp((time - run.Start.Value).TotalMilliseconds / DurationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);

        control.Height = run.FromHeight + ((run.ToHeight - run.FromHeight) * eased);
        control.Opacity = run.FromOpacity + (((run.Open ? 1 : 0) - run.FromOpacity) * eased);
        var transform = control.RenderTransform as TranslateTransform ?? new TranslateTransform();
        transform.Y = run.Open ? -Slide * (1 - eased) : -Slide * eased;
        control.RenderTransform = transform;

        if (progress < 1 && TopLevel.GetTopLevel(control) is { } top)
        {
            top.RequestAnimationFrame(t => Step(control, t));
            return;
        }

        // Settled: give the height back to layout (the content may still grow), or hide.
        Runs.Remove(control);
        control.IsVisible = run.Open;
        control.Height = double.NaN;
        control.Opacity = 1;
        transform.Y = 0;
    }
}
