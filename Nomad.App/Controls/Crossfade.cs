using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// <c>c:Crossfade.IsShown</c>: for views stacked in one place that take turns (a chat and its Files view). The one
/// shown fades in, sliding <c>c:Crossfade.Slide</c> pixels sideways into place; the other fades out, stops taking
/// input at once and is hidden when the fade ends (200 ms, cubic ease-out). A change mid-flight retargets.
/// </summary>
public static class Crossfade
{
    public static readonly AttachedProperty<bool> IsShownProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsShown", typeof(Crossfade), defaultValue: true);

    /// <summary>Where it slides in from: negative from the left, positive from the right.</summary>
    public static readonly AttachedProperty<double> SlideProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Slide", typeof(Crossfade));

    private const double DurationMs = 200;

    private sealed class Run
    {
        public bool Shown;
        public double From;
        public TimeSpan? Start;
    }

    private static readonly Dictionary<Control, Run> Runs = [];

    static Crossfade()
    {
        IsShownProperty.Changed.AddClassHandler<Control>((control, e) => Begin(control, e.GetNewValue<bool>()));
    }

    public static bool GetIsShown(Control control) => control.GetValue(IsShownProperty);

    public static void SetIsShown(Control control, bool value) => control.SetValue(IsShownProperty, value);

    public static double GetSlide(Control control) => control.GetValue(SlideProperty);

    public static void SetSlide(Control control, double value) => control.SetValue(SlideProperty, value);

    private static void Begin(Control control, bool shown)
    {
        control.IsHitTestVisible = shown;

        // Not on screen yet (a template being built): take the state at once.
        if (TopLevel.GetTopLevel(control) is not { } top || (control.Parent as Control)?.Bounds.Width is null or <= 0)
        {
            Runs.Remove(control);
            control.IsVisible = shown;
            control.Opacity = shown ? 1 : 0;
            control.RenderTransform = null;
            return;
        }

        var running = Runs.ContainsKey(control);
        var from = control.IsVisible ? control.Opacity : 0;
        control.IsVisible = true;
        Runs[control] = new Run { Shown = shown, From = from };
        if (!running)
        {
            top.RequestAnimationFrame(time => Step(control, top, time));
        }
    }

    private static void Step(Control control, TopLevel top, TimeSpan time)
    {
        if (!Runs.TryGetValue(control, out var run))
        {
            return;
        }

        run.Start ??= time;
        var p = Math.Clamp((time - run.Start.Value).TotalMilliseconds / DurationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - p, 3);
        var to = run.Shown ? 1.0 : 0.0;
        var opacity = run.From + (to - run.From) * eased;
        control.Opacity = opacity;

        // Only the view coming in slides; the one going just fades.
        var slide = GetSlide(control);
        control.RenderTransform = run.Shown && slide != 0 ? new TranslateTransform(slide * (1 - opacity), 0) : null;

        if (p < 1)
        {
            top.RequestAnimationFrame(t => Step(control, top, t));
            return;
        }

        Runs.Remove(control);
        control.RenderTransform = null;
        control.IsVisible = run.Shown;
    }
}
