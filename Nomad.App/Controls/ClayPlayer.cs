using Avalonia;
using Avalonia.Media;
using Nomad.App.Infrastructure;

namespace Nomad.App.Controls;

/// <summary>
/// Plays the desktop app's working mark (<see cref="WorkingMarkSheets"/>) as its script does: an activity comes
/// "in", "loop"s while it lasts, and goes "out" before the next comes in. A change waits for the loop's next seam
/// where it has seams; otherwise a loop runs at least 2 s, and with more than 1.5 s left it crossfades (300 ms)
/// into its last 20 frames and goes out from there. 30 frames a second.
/// </summary>
public sealed class ClayPlayer
{
    private enum Kind
    {
        In,
        Loop,
        Tail,
        Out,
    }

    private const int TailFrames = 20;
    private const double MinLoopMs = 2000;
    private const double LoopLeftForTailMs = 1500;
    private const double CrossfadeMs = 300;

    private readonly IReadOnlyDictionary<string, MarkSheet> _sheets;
    private string _wanted = "default";
    private Kind _kind = Kind.In;
    private string _body = "default";
    private double _stageStart;
    private double _loopStart;

    // The frame a crossfade starts from, drawn under the new stage while it fades in.
    private (MarkSheet Sheet, int Frame)? _fadeFrom;
    private double _fadeStart;

    public ClayPlayer(IReadOnlyDictionary<string, MarkSheet> sheets, string activity, double now)
    {
        _sheets = sheets;
        _wanted = _body = Resolve(activity);
        _stageStart = now;
    }

    /// <summary>"think", "read", "search", "code", "write", "compose", "blocked" or "default".</summary>
    public void SetActivity(string activity) => _wanted = Resolve(activity);

    private string Resolve(string activity) => _sheets.ContainsKey(activity + ".loop") ? activity : "default";

    private MarkSheet Sheet(Kind kind, string body) =>
        _sheets.GetValueOrDefault($"{body}.{(kind == Kind.Tail ? "loop" : kind.ToString().ToLowerInvariant())}") ?? _sheets["default.loop"];

    private (int First, int Count) Range(MarkSheet sheet) =>
        _kind == Kind.Tail ? (Math.Max(0, sheet.Frames - TailFrames), Math.Min(TailFrames, sheet.Frames)) : (0, sheet.Frames);

    /// <summary>Moves the stages on to <paramref name="now"/> (milliseconds on any steady clock).</summary>
    public void Advance(double now)
    {
        for (var guard = 0; guard < 8; guard++)
        {
            var sheet = Sheet(_kind, _body);
            var (_, count) = Range(sheet);
            var elapsed = now - _stageStart;

            if (_kind == Kind.Loop && _wanted != _body && LeaveLoopEarly(sheet, elapsed, now))
            {
                continue;
            }

            var duration = count * sheet.FrameMs;
            if (elapsed < duration)
            {
                return;
            }

            // A long stall (the window hidden) starts over instead of replaying every missed stage.
            _stageStart = now - _stageStart > 4 * duration ? now : _stageStart + duration;
            switch (_kind)
            {
                case Kind.In:
                    _kind = Kind.Loop;
                    _loopStart = _stageStart;
                    break;
                case Kind.Loop when _wanted == _body:
                    break; // Another round.
                case Kind.Loop or Kind.Tail:
                    _kind = Kind.Out;
                    break;
                case Kind.Out:
                    _kind = Kind.In;
                    _body = _wanted;
                    break;
            }
        }
    }

    private bool LeaveLoopEarly(MarkSheet sheet, double elapsed, double now)
    {
        var frame = (int)(elapsed / sheet.FrameMs);
        var plain = _body is "default" or "blocked" || _wanted is "default" or "blocked";
        if (!plain)
        {
            // Out at the loop's next seam, where its pose matches the out stage's first frame.
            if (sheet.Seams.Length == 0 || !sheet.Seams.Any(s => s * sheet.FrameMs <= elapsed && s >= frame - 1))
            {
                return false;
            }

            _kind = Kind.Out;
            _stageStart = now;
            return true;
        }

        if (now - _loopStart < MinLoopMs || sheet.DurationMs - elapsed <= LoopLeftForTailMs)
        {
            return false;
        }

        _fadeFrom = (sheet, Math.Min(sheet.Frames - 1, frame));
        _fadeStart = now;
        _kind = Kind.Tail;
        _stageStart = now;
        return true;
    }

    /// <summary>The current frame (crossfading from an earlier one while a stage change asks for it).</summary>
    public void Draw(DrawingContext context, Rect rect, double now, double opacity = 1)
    {
        var sheet = Sheet(_kind, _body);
        var (first, count) = Range(sheet);
        var step = (int)((now - _stageStart) / sheet.FrameMs);
        var frame = first + (_kind == Kind.Loop ? step % count : Math.Clamp(step, 0, count - 1));

        var fade = 1.0;
        if (_fadeFrom is { } from)
        {
            var t = (now - _fadeStart) / CrossfadeMs;
            if (t >= 1)
            {
                _fadeFrom = null;
            }
            else
            {
                // ease-in-out, as the script's 300 ms opacity animation
                fade = t < 0.5 ? 2 * t * t : 1 - (Math.Pow(-2 * t + 2, 2) / 2);
                DrawFrame(context, from.Sheet, from.Frame, rect, opacity);
            }
        }

        DrawFrame(context, sheet, frame, rect, opacity * fade);
    }

    private static void DrawFrame(DrawingContext context, MarkSheet sheet, int frame, Rect rect, double opacity)
    {
        if (opacity <= 0)
        {
            return;
        }

        var size = sheet.FrameSize;
        using (context.PushOpacity(opacity))
        {
            context.DrawImage(sheet.Strip, new Rect(0, frame * size, size, size), rect);
        }
    }
}
