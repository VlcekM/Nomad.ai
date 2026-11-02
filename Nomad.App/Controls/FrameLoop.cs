using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Nomad.App.Controls;

/// <summary>
/// A control's animation on the window's render frames: <paramref name="frame"/> runs once per frame while the loop is
/// wanted and the control is in a window, and nothing runs otherwise. Frames are what the compositor presents, so the
/// animation is as smooth as the display (a timer's ticks land on the 15.6 ms system tick and never line up with them).
/// </summary>
internal sealed class FrameLoop(Control owner, Action<TimeSpan> frame)
{
    private bool _wanted;
    private bool _scheduled;

    /// <summary>Starts the loop, or lets it stop at the next frame.</summary>
    public void Update(bool wanted)
    {
        _wanted = wanted;
        if (wanted && !_scheduled && TopLevel.GetTopLevel(owner) is { } top)
        {
            _scheduled = true;
            top.RequestAnimationFrame(Step);
        }
    }

    public void Stop() => _wanted = false;

    private void Step(TimeSpan time)
    {
        _scheduled = false;
        if (!_wanted || owner.GetVisualRoot() is null)
        {
            return;
        }

        // The frame may itself call Update (a state check): then the next frame is already scheduled.
        frame(time);
        if (_wanted && !_scheduled && TopLevel.GetTopLevel(owner) is { } top)
        {
            _scheduled = true;
            top.RequestAnimationFrame(Step);
        }
    }
}
