using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// A control that just moved glides from where it was into its new place (cubic ease-out, on render frames): it starts
/// translated by <c>offset</c> (old position minus new) and eases to none; with <c>fade</c> it also fades in. A new
/// glide on the same control takes over.
/// </summary>
public static class Glide
{
    public static void Play(Control control, Vector offset, double durationMs = 220, Action? done = null, bool fade = false)
    {
        if (TopLevel.GetTopLevel(control) is not { } top)
        {
            if (fade)
            {
                control.Opacity = 1;
            }

            done?.Invoke();
            return;
        }

        var transform = new TranslateTransform(offset.X, offset.Y);
        control.RenderTransform = transform;
        if (fade)
        {
            control.Opacity = 0;
        }

        TimeSpan? start = null;

        void Step(TimeSpan time)
        {
            if (control.RenderTransform != transform)
            {
                return;
            }

            start ??= time;
            var t = Math.Clamp((time - start.Value).TotalMilliseconds / durationMs, 0, 1);
            var rest = Math.Pow(1 - t, 3);
            transform.X = offset.X * rest;
            transform.Y = offset.Y * rest;
            if (fade)
            {
                control.Opacity = 1 - rest;
            }

            if (t < 1)
            {
                top.RequestAnimationFrame(Step);
            }
            else
            {
                control.RenderTransform = null;
                if (fade)
                {
                    control.Opacity = 1;
                }

                done?.Invoke();
            }
        }

        top.RequestAnimationFrame(Step);
    }
}
