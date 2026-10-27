using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>A new transcript item fades in and slides up into place (220 ms, cubic ease-out), on render frames.</summary>
public static class Entrance
{
    private const double DurationMs = 220;
    private const double Rise = 10;

    public static void Play(Control control)
    {
        if (TopLevel.GetTopLevel(control) is not { } top)
        {
            return;
        }

        var transform = new TranslateTransform(0, Rise);
        control.RenderTransform = transform;
        control.Opacity = 0;
        TimeSpan? start = null;

        void Step(TimeSpan time)
        {
            start ??= time;
            var p = Math.Clamp((time - start.Value).TotalMilliseconds / DurationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - p, 3);
            control.Opacity = eased;
            transform.Y = Rise * (1 - eased);
            if (p < 1)
            {
                top.RequestAnimationFrame(Step);
            }
            else
            {
                control.RenderTransform = null;
            }
        }

        top.RequestAnimationFrame(Step);
    }

    /// <summary>Only the fade (150 ms), for a row joining a list that should not seem to move.</summary>
    public static void Fade(Control control, TopLevel? topLevel = null)
    {
        if ((topLevel ?? TopLevel.GetTopLevel(control)) is not { } top)
        {
            return;
        }

        control.Opacity = 0;
        TimeSpan? start = null;

        void Step(TimeSpan time)
        {
            start ??= time;
            var p = Math.Clamp((time - start.Value).TotalMilliseconds / 150, 0, 1);
            control.Opacity = 1 - Math.Pow(1 - p, 3);
            if (p < 1)
            {
                top.RequestAnimationFrame(Step);
            }
        }

        top.RequestAnimationFrame(Step);
    }
}
