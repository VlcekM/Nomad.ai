using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// Middle-button autoscroll, as in a browser (on for every <see cref="ScrollViewer"/> through a style in
/// Styles.axaml). Pressing the middle button drops a marker; the further the pointer is above or below it, the
/// faster the view scrolls that way. Held and dragged, it stops on release; clicked, it keeps going until the next
/// click, a wheel turn or Esc. Runs on render frames only while active.
/// </summary>
public static class AutoScroll
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(AutoScroll));

    /// <summary>No scrolling this close to the marker.</summary>
    private const double DeadZone = 10;

    /// <summary>A press shorter than this, without moving, is a click: the scroll stays on.</summary>
    private const double ClickMs = 300;

    private static Session? _active;

    static AutoScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
        {
            viewer.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            if (e.NewValue is true)
            {
                viewer.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            }
        });
    }

    public static bool GetIsEnabled(ScrollViewer viewer) => viewer.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer viewer, bool value) => viewer.SetValue(IsEnabledProperty, value);

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !e.GetCurrentPoint(viewer).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        // Tunnelling, the outermost scroller that can move takes it (the transcript, not a code block inside it).
        if (_active is not null || viewer.Extent.Height <= viewer.Viewport.Height + 1)
        {
            return;
        }

        e.Handled = true;
        _active = new Session(viewer, e.GetPosition(viewer), e.Pointer);
    }

    private sealed class Session
    {
        private readonly ScrollViewer _viewer;
        private readonly Point _anchor;
        private readonly TopLevel? _top;
        private readonly Marker _marker;
        private readonly Cursor? _cursor;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Point _pointer;
        private bool _held = true;
        private bool _moved;
        private TimeSpan? _lastFrame;
        private bool _ended;

        public Session(ScrollViewer viewer, Point anchor, IPointer pointer)
        {
            _viewer = viewer;
            _anchor = _pointer = anchor;
            _top = TopLevel.GetTopLevel(viewer);
            _cursor = viewer.Cursor;
            viewer.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            pointer.Capture(viewer);

            _marker = new Marker(anchor, _clock);
            if (AdornerLayer.GetAdornerLayer(viewer) is { } layer)
            {
                AdornerLayer.SetAdornedElement(_marker, viewer);
                layer.Children.Add(_marker);
            }

            // On the top level, so a click mode sees the pointer anywhere and its next click (or wheel, or Esc) ends it.
            _top?.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            _top?.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            _top?.AddHandler(InputElement.PointerPressedEvent, OnAnyPress, RoutingStrategies.Tunnel);
            _top?.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            _top?.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
            _top?.RequestAnimationFrame(Frame);
        }

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            _pointer = e.GetPosition(_viewer);
            _moved |= Math.Abs(_pointer.Y - _anchor.Y) > DeadZone;
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_held || e.InitialPressMouseButton != MouseButton.Middle)
            {
                return;
            }

            _held = false;
            e.Pointer.Capture(null);
            if (_moved || _clock.Elapsed.TotalMilliseconds > ClickMs)
            {
                End();
            }
        }

        private void OnAnyPress(object? sender, PointerPressedEventArgs e)
        {
            if (!_held)
            {
                e.Handled = true; // The click that ends it does nothing else.
                End();
            }
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e) => End();

        private void OnKey(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
            }

            End();
        }

        private void Frame(TimeSpan time)
        {
            if (_ended)
            {
                return;
            }

            var dt = _lastFrame is { } last ? Math.Min(0.05, (time - last).TotalSeconds) : 0;
            _lastFrame = time;

            // Speed grows faster than the distance, as in a browser: 50 px out about 60 px/s, 200 px about 650.
            var distance = _pointer.Y - _anchor.Y;
            var beyond = Math.Max(0, Math.Abs(distance) - DeadZone);
            var speed = Math.Sign(distance) * Math.Pow(beyond, 1.45) * 0.3;
            if (speed != 0 && dt > 0)
            {
                var max = Math.Max(0, _viewer.Extent.Height - _viewer.Viewport.Height);
                var y = Math.Clamp(_viewer.Offset.Y + (speed * dt), 0, max);
                _viewer.Offset = new Vector(_viewer.Offset.X, y);
            }

            _marker.InvalidateVisual();
            _top?.RequestAnimationFrame(Frame);
        }

        private void End()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            _active = null;
            _viewer.Cursor = _cursor;
            (_marker.Parent as Panel)?.Children.Remove(_marker);
            _top?.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            _top?.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            _top?.RemoveHandler(InputElement.PointerPressedEvent, OnAnyPress);
            _top?.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            _top?.RemoveHandler(InputElement.KeyDownEvent, OnKey);
        }
    }

    /// <summary>The anchor: a small disc with an arrow up and down, faded in over 150 ms.</summary>
    private sealed class Marker(Point anchor, Stopwatch clock) : Control
    {
        private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#E6262626"));
        private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.Parse("#5A5A5A")), 1);
        private static readonly IBrush Arrow = new SolidColorBrush(Color.Parse("#E8E8E8"));

        public override void Render(DrawingContext context)
        {
            var t = Math.Clamp(clock.Elapsed.TotalMilliseconds / 150, 0, 1);
            using (context.PushOpacity(1 - Math.Pow(1 - t, 3)))
            {
                context.DrawEllipse(Fill, Edge, anchor, 13, 13);
                context.DrawGeometry(Arrow, null, Triangle(anchor + new Point(0, -6), up: true));
                context.DrawGeometry(Arrow, null, Triangle(anchor + new Point(0, 6), up: false));
                context.DrawEllipse(Arrow, null, anchor, 1.5, 1.5);
            }
        }

        private static StreamGeometry Triangle(Point tip, bool up)
        {
            var geometry = new StreamGeometry();
            using var g = geometry.Open();
            var dy = up ? 4 : -4;
            g.BeginFigure(tip, true);
            g.LineTo(new Point(tip.X - 4, tip.Y + dy));
            g.LineTo(new Point(tip.X + 4, tip.Y + dy));
            g.EndFigure(true);
            return geometry;
        }
    }
}
