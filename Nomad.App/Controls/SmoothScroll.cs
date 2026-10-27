using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Nomad.App.Controls;

/// <summary>
/// Smooth mouse-wheel scrolling for a <see cref="ScrollViewer"/> (on for all of them through a style
/// in Styles.axaml). A wheel notch moves a target; the offset eases toward it each frame, and further
/// notches extend the target instead of restarting. A nested scroller that can still move takes the
/// wheel itself; at its end the wheel passes on to the outer one.
/// </summary>
public static class SmoothScroll
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(SmoothScroll));

    private const double NotchPixels = 110;

    /// <summary>Time constant of the ease-out: about 63% of the remaining distance per this many ms.</summary>
    private const double EaseMs = 60;

    private static readonly ConditionalWeakTable<ScrollViewer, Glide> Glides = new();

    static SmoothScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
        {
            viewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            if (e.NewValue is true)
            {
                viewer.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            }
        });
    }

    public static bool GetIsEnabled(ScrollViewer viewer) => viewer.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer viewer, bool value) => viewer.SetValue(IsEnabledProperty, value);

    private sealed class Glide
    {
        public double Target;

        /// <summary>A frame callback is pending: the glide runs.</summary>
        public bool IsRunning;

        /// <summary>Time of the previous frame (null on the first frame of a glide).</summary>
        public TimeSpan? LastFrame;
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.Handled || e.KeyModifiers != KeyModifiers.None || e.Delta.Y == 0)
        {
            return;
        }

        // Tunnelling reaches the outer viewer first: leave the wheel to an inner one that can move.
        if (InnerCanTake(viewer, e))
        {
            return;
        }

        var max = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        var glide = Glides.GetValue(viewer, _ => new Glide());
        var running = glide.IsRunning;
        var from = running ? glide.Target : viewer.Offset.Y;
        var target = Math.Clamp(from - e.Delta.Y * NotchPixels, 0, max);
        if (Math.Abs(target - from) < 0.5 && !running)
        {
            return; // At the end: let the wheel reach whatever is outside.
        }

        e.Handled = true;
        glide.Target = target;
        if (!running && TopLevel.GetTopLevel(viewer) is { } top)
        {
            glide.IsRunning = true;
            glide.LastFrame = null;
            top.RequestAnimationFrame(time => Step(viewer, glide, time));
        }
    }

    /// <summary>
    /// One rendered frame (at the display's refresh rate): cover a share of the remaining distance
    /// that depends on the time since the last frame, so the ease-out looks the same at 60 or 144 Hz
    /// and retargets cleanly.
    /// </summary>
    private static void Step(ScrollViewer viewer, Glide glide, TimeSpan time)
    {
        var y = viewer.Offset.Y;
        var left = glide.Target - y;
        if (Math.Abs(left) < 0.5 || TopLevel.GetTopLevel(viewer) is not { } top)
        {
            viewer.Offset = viewer.Offset.WithY(glide.Target);
            glide.IsRunning = false;
            return;
        }

        // The first frame has no previous one: assume a 144 Hz frame.
        var dt = glide.LastFrame is { } last ? Math.Clamp((time - last).TotalMilliseconds, 0, 50) : 1000.0 / 144;
        glide.LastFrame = time;
        var share = 1 - Math.Exp(-dt / EaseMs);
        viewer.Offset = viewer.Offset.WithY(y + left * share);
        top.RequestAnimationFrame(t => Step(viewer, glide, t));
    }

    private static bool InnerCanTake(ScrollViewer outer, PointerWheelEventArgs e)
    {
        for (var visual = e.Source as Visual; visual is not null && !ReferenceEquals(visual, outer); visual = visual.GetVisualParent())
        {
            if (visual is ScrollViewer inner && inner.Extent.Height > inner.Viewport.Height + 0.5)
            {
                var max = inner.Extent.Height - inner.Viewport.Height;
                var y = inner.Offset.Y;
                if ((e.Delta.Y > 0 && y > 0.5) || (e.Delta.Y < 0 && y < max - 0.5))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
