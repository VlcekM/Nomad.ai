using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Nomad.App.Controls;

/// <summary>
/// Middle-button autoscroll, as in a browser (on for every <see cref="ScrollViewer"/> through a style in
/// Styles.axaml). Pressing the middle button drops a marker; the further the pointer is from it, the faster the view
/// scrolls that way, up and down and (where the content, or a table or code block under the pointer, is wider than the view) sideways. Held and dragged, it stops on release; clicked, it keeps going until the next
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
        var vertical = viewer.Extent.Height > viewer.Viewport.Height + 1;
        var horizontal = CanScrollSideways(viewer);
        if (_active is not null || !vertical && !horizontal)
        {
            return;
        }

        // One that only scrolls up and down hands sideways to a wide table or code block under the pointer.
        var sideways = horizontal ? viewer : SidewaysInside(viewer, e.Source as Visual);
        e.Handled = true;
        _active = new Session(viewer, sideways, e.GetPosition(viewer), e.Pointer, vertical);
    }

    private static bool CanScrollSideways(ScrollViewer viewer) => viewer.Extent.Width > viewer.Viewport.Width + 1;

    // The innermost scroller between the pressed visual and the outer one that is wider than its view.
    private static ScrollViewer? SidewaysInside(ScrollViewer outer, Visual? pressed)
    {
        for (var visual = pressed; visual is not null && visual != outer; visual = visual.GetVisualParent())
        {
            if (visual is ScrollViewer inner && CanScrollSideways(inner))
            {
                return inner;
            }
        }

        return null;
    }

    // Speed grows faster than the distance, as in a browser but 6× quicker: 50 px out about 380 px/s, 200 px about 3600.
    private static double Speed(double distance)
    {
        var beyond = Math.Max(0, Math.Abs(distance) - DeadZone);
        return Math.Sign(distance) * Math.Pow(beyond, 1.45) * 1.8;
    }

    private sealed class Session
    {
        private readonly ScrollViewer _viewer;
        private readonly ScrollViewer? _sideways;
        private readonly Point _anchor;
        private readonly bool _vertical;
        private readonly bool _horizontal;
        private readonly TopLevel? _top;
        private readonly Marker _marker;
        private readonly Cursor? _cursor;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Point _pointer;
        private bool _held = true;
        private bool _moved;
        private TimeSpan? _lastFrame;
        private bool _ended;

        public Session(ScrollViewer viewer, ScrollViewer? sideways, Point anchor, IPointer pointer, bool vertical)
        {
            _viewer = viewer;
            _sideways = sideways;
            _anchor = _pointer = anchor;
            _vertical = vertical;
            _horizontal = sideways is not null;
            _top = TopLevel.GetTopLevel(viewer);
            _cursor = viewer.Cursor;
            viewer.Cursor = new Cursor(vertical && _horizontal ? StandardCursorType.SizeAll
                : vertical ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
            pointer.Capture(viewer);

            // Placed in the layer's own coordinates: an adorned element's offset is not applied to it in time.
            var layer = AdornerLayer.GetAdornerLayer(viewer);
            var at = layer is null ? anchor : viewer.TranslatePoint(anchor, layer) ?? anchor;
            _marker = new Marker(_clock, vertical, _horizontal) { Width = Marker.Size, Height = Marker.Size };
            Canvas.SetLeft(_marker, at.X - Marker.Size / 2);
            Canvas.SetTop(_marker, at.Y - Marker.Size / 2);
            layer?.Children.Add(_marker);

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
            _moved |= _vertical && Math.Abs(_pointer.Y - _anchor.Y) > DeadZone || _horizontal && Math.Abs(_pointer.X - _anchor.X) > DeadZone;
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

            var dx = _horizontal ? Speed(_pointer.X - _anchor.X) * dt : 0;
            var dy = _vertical ? Speed(_pointer.Y - _anchor.Y) * dt : 0;
            if (dy != 0)
            {
                Move(_viewer, 0, dy);
            }

            if (dx != 0 && _sideways is not null)
            {
                Move(_sideways, dx, 0);
            }

            _marker.InvalidateVisual();
            _top?.RequestAnimationFrame(Frame);
        }

        private static void Move(ScrollViewer viewer, double dx, double dy)
        {
            var maxX = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
            var maxY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            viewer.Offset = new Vector(Math.Clamp(viewer.Offset.X + dx, 0, maxX), Math.Clamp(viewer.Offset.Y + dy, 0, maxY));
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

    /// <summary>The anchor: a small disc with an arrow each way it scrolls, faded in over 150 ms.</summary>
    private sealed class Marker(Stopwatch clock, bool vertical, bool horizontal) : Control
    {
        public const double Size = 32;

        private static readonly Point Center = new(Size / 2, Size / 2);

        private static IBrush Fill => ThemeBrush("JumpFillBrush", "#E6262626");

        private static IPen Edge => new Pen(ThemeBrush("JumpEdgeBrush", "#5A5A5A"), 1);

        private static IBrush Arrow => ThemeBrush("JumpArrowBrush", "#E8E8E8");

        private static IBrush ThemeBrush(string key, string fallback) =>
            Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
                ? brush
                : new SolidColorBrush(Color.Parse(fallback));

        public override void Render(DrawingContext context)
        {
            var t = Math.Clamp(clock.Elapsed.TotalMilliseconds / 150, 0, 1);
            using (context.PushOpacity(1 - Math.Pow(1 - t, 3)))
            {
                context.DrawEllipse(Fill, Edge, Center, 13, 13);
                if (vertical)
                {
                    context.DrawGeometry(Arrow, null, Triangle(Center, new Vector(0, -1)));
                    context.DrawGeometry(Arrow, null, Triangle(Center, new Vector(0, 1)));
                }

                if (horizontal)
                {
                    context.DrawGeometry(Arrow, null, Triangle(Center, new Vector(-1, 0)));
                    context.DrawGeometry(Arrow, null, Triangle(Center, new Vector(1, 0)));
                }

                context.DrawEllipse(Arrow, null, Center, 1.5, 1.5);
            }
        }

        // An arrow pointing along a unit direction, its tip 6 px out from the centre.
        private static StreamGeometry Triangle(Point center, Vector direction)
        {
            var geometry = new StreamGeometry();
            using var g = geometry.Open();
            var tip = center + direction * 6;
            var back = tip - direction * 4;
            var side = new Vector(-direction.Y, direction.X) * 4;
            g.BeginFigure(tip, true);
            g.LineTo(back + side);
            g.LineTo(back - side);
            g.EndFigure(true);
            return geometry;
        }
    }
}
