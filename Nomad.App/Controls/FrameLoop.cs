using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Nomad.App.Controls;

/// <summary>
/// A control's animation on the window's render frames: <paramref name="frame"/> runs once per frame while the loop is
/// wanted and the control is in a window, and nothing runs otherwise. Frames are what the compositor presents, so the
/// animation is as smooth as the display (a timer's ticks land on the 15.6 ms system tick and never line up with them).
/// </summary>
internal sealed class FrameLoop
{
    private readonly Control _owner;
    private readonly Action<TimeSpan> _frame;
    private bool _wanted;
    private bool _scheduled;

    public FrameLoop(Control owner, Action<TimeSpan> frame)
    {
        _owner = owner;
        _frame = frame;

        // Wanted before the control was in a window: it starts once there is one.
        owner.AttachedToVisualTree += (_, _) => Update(_wanted);
    }

    /// <summary>Starts the loop, or lets it stop at the next frame.</summary>
    public void Update(bool wanted)
    {
        _wanted = wanted;
        if (wanted && !_scheduled && TopLevel.GetTopLevel(_owner) is { } top)
        {
            _scheduled = true;
            top.RequestAnimationFrame(Step);
        }
    }

    public void Stop() => _wanted = false;

    private void Step(TimeSpan time)
    {
        _scheduled = false;
        if (!_wanted || _owner.GetVisualRoot() is null)
        {
            return;
        }

        // The frame may itself call Update (a state check): then the next frame is already scheduled.
        _frame(time);
        if (_wanted && !_scheduled && TopLevel.GetTopLevel(_owner) is { } top)
        {
            _scheduled = true;
            top.RequestAnimationFrame(Step);
        }
    }
}
