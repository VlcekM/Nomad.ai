using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Utilities;
using Avalonia.VisualTree;

namespace Nomad.App.Controls;

/// <summary>
/// A vertical virtualizing stack for the transcript. Avalonia's <c>VirtualizingStackPanel</c> guesses every item it
/// has not built as the average of those it has, so in a chat of one-line tool rows and pages-long answers the
/// scrollbar grew and shrank, and the view jumped, with every item scrolled past. This one keeps a height for every
/// item and puts each where the heights before it say: a guess (the average of its kind) until the item is built,
/// then its real height. The items not yet built are measured a few at a time while the app is idle, so the heights
/// are soon all real; a change above the view is taken up by the scroll viewer's anchoring, so the view stays put.
/// </summary>
public sealed class TranscriptPanel : VirtualizingPanel
{
    // Built beyond the viewport on either side, in viewports.
    private const double CacheLength = 1;

    // A guess before any item has been built.
    private const double FirstGuess = 60;

    // Time to spend measuring ahead per idle turn.
    private static readonly TimeSpan AheadBudget = TimeSpan.FromMilliseconds(8);

    private static readonly AttachedProperty<object?> RecycleKeyProperty =
        AvaloniaProperty.RegisterAttached<TranscriptPanel, Control, object?>("RecycleKey");

    private static readonly object ItemIsItsOwnContainer = new();

    // Every item's height, parallel to Items: real once it has been measured at the current width (_known), a guess
    // until then. A guess is made once, so items never move for a guess about another item changing.
    private readonly List<double> _heights = [];
    private readonly List<bool> _known = [];

    // Real heights by item type, for guessing.
    private readonly Dictionary<Type, (double Sum, int Count)> _byType = [];
    private double _sum;
    private int _count;

    // The built items: Items[_first + i] is _elements[i] (null where an item was inserted since).
    private readonly List<Control?> _elements = [];
    private int _first;
    private double _startU;
    private double _width = double.NaN;

    private readonly Dictionary<object, Stack<Control>> _recyclePool = [];
    private IScrollAnchorProvider? _anchorProvider;
    private Rect _viewport;
    private Rect _extended;
    private bool _isInLayout;
    private bool _aheadPosted;
    private Control? _realizing;
    private int _realizingIndex = -1;

    public TranscriptPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    /// <summary>True while an item out of view is built only to be measured: its container is not shown.</summary>
    public bool IsMeasuringAhead { get; private set; }

    /// <summary>Measures every item again, as when what the items show changes (Show activity).</summary>
    public void ForgetHeights()
    {
        ForgetAll();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        if (items.Count == 0)
        {
            RecycleAll();
            return default;
        }

        _isInLayout = true;
        try
        {
            SyncHeights(items);
            var width = availableSize.Width;
            if (!MathUtilities.AreClose(width, _width))
            {
                // Another width wraps the text differently: the heights so far stay as guesses.
                if (!double.IsNaN(_width))
                {
                    ForgetAll();
                }

                _width = width;
            }

            var childSize = new Size(width, double.PositiveInfinity);
            var start = _extended.Top;
            var end = Math.Max(_extended.Bottom, start + 1);

            // The item at the top of the area to build, and where the heights put it.
            var anchor = 0;
            var anchorU = 0.0;
            while (anchor < items.Count - 1 && anchorU + _heights[anchor] <= start)
            {
                anchorU += _heights[anchor];
                anchor++;
            }

            // Out of the built range altogether: recycle first, so the new items can reuse the containers.
            var old = new List<Control?>(_elements);
            var oldFirst = _first;
            if (anchor < oldFirst || anchor > oldFirst + old.Count - 1)
            {
                RecycleRange(old);
            }

            _elements.Clear();
            var maxWidth = 0.0;

            // Forwards from the anchor to the end of the area...
            var index = anchor;
            var u = anchorU;
            var below = new List<Control>();
            do
            {
                var element = Realize(items, index, old, oldFirst);
                element.Measure(childSize);
                Remember(items, index, element.DesiredSize.Height);
                maxWidth = Math.Max(maxWidth, element.DesiredSize.Width);
                below.Add(element);
                u += element.DesiredSize.Height;
                index++;
            }
            while (u < end && index < items.Count);

            // ...and backwards from it to the start.
            var above = new List<Control>();
            index = anchor - 1;
            u = anchorU;
            while (u > start && index >= 0)
            {
                var element = Realize(items, index, old, oldFirst);
                element.Measure(childSize);
                Remember(items, index, element.DesiredSize.Height);
                maxWidth = Math.Max(maxWidth, element.DesiredSize.Width);
                above.Add(element);
                u -= element.DesiredSize.Height;
                index--;
            }

            above.Reverse();
            _first = index + 1;
            _elements.AddRange(above);
            _elements.AddRange(below);

            // Whatever was built before and is not now goes back to the pool.
            RecycleRange(old);

            // The items above the built ones have not changed, so the built ones start where they did; those that
            // changed height move what is under them, and the scroll viewer's anchoring keeps the view on it.
            _startU = 0;
            for (var i = 0; i < _first; i++)
            {
                _startU += _heights[i];
            }

            var total = 0.0;
            var unknown = false;
            for (var i = 0; i < items.Count; i++)
            {
                total += _heights[i];
                unknown |= !_known[i];
            }

            if (unknown)
            {
                MeasureAheadSoon();
            }

            return new Size(double.IsInfinity(width) ? maxWidth : Math.Max(0, width), total);
        }
        finally
        {
            _isInLayout = false;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _isInLayout = true;
        try
        {
            var u = _startU;
            foreach (var element in _elements)
            {
                if (element is null)
                {
                    continue;
                }

                var rect = new Rect(0, u, finalSize.Width, element.DesiredSize.Height);
                element.Arrange(rect);
                if (element.IsVisible && _viewport.Intersects(rect))
                {
                    try
                    {
                        _anchorProvider?.RegisterAnchorCandidate(element);
                    }
                    catch (InvalidOperationException)
                    {
                        // Moved out from under the scroll viewer mid-layout.
                    }
                }

                u += element.DesiredSize.Height;
            }

            return finalSize;
        }
        finally
        {
            _isInLayout = false;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _anchorProvider = this.FindAncestorOfType<IScrollAnchorProvider>();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _anchorProvider = null;
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        InvalidateMeasure();
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex >= 0 && e.NewStartingIndex <= _heights.Count:
                Inserted(e.NewStartingIndex, e.NewItems!);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0 && e.OldStartingIndex + e.OldItems!.Count <= _heights.Count:
                Removed(e.OldStartingIndex, e.OldItems!);
                break;
            case NotifyCollectionChangedAction.Replace when e.OldStartingIndex >= 0 && e.OldStartingIndex + e.OldItems!.Count <= _heights.Count:
                Removed(e.OldStartingIndex, e.OldItems!);
                Inserted(e.OldStartingIndex, e.NewItems!);
                break;
            default:
                // Measure starts the heights over; the averages stay, as better first guesses than none.
                RecycleAll();
                _heights.Clear();
                _known.Clear();
                break;
        }
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _elements.OfType<Control>();

    protected override Control? ContainerFromIndex(int index)
    {
        if (index == _realizingIndex)
        {
            return _realizing;
        }

        var i = index - _first;
        return i >= 0 && i < _elements.Count ? _elements[i] : null;
    }

    protected override int IndexFromContainer(Control container)
    {
        if (container == _realizing)
        {
            return _realizingIndex;
        }

        var i = _elements.IndexOf(container);
        return i < 0 ? -1 : _first + i;
    }

    protected override Control? ScrollIntoView(int index)
    {
        var items = Items;
        if (_isInLayout || index < 0 || index >= items.Count || !IsEffectivelyVisible)
        {
            return null;
        }

        if (ContainerFromIndex(index) is { } built)
        {
            built.BringIntoView();
            return built;
        }

        // Scroll to where the heights put it; building the items there may move it a little, so once built it is
        // brought into view again.
        SyncHeights(items);
        var u = 0.0;
        for (var i = 0; i < index; i++)
        {
            u += _heights[i];
        }

        RaiseEvent(new RequestBringIntoViewEventArgs
        {
            RoutedEvent = RequestBringIntoViewEvent,
            TargetObject = this,
            TargetRect = new Rect(0, u, Bounds.Width, _heights[index]),
        });
        UpdateLayout();
        if (ContainerFromIndex(index) is { } made)
        {
            made.BringIntoView();
            UpdateLayout();
            return made;
        }

        return null;
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Items.Count;
        var fromIndex = from is Control control ? IndexFromContainer(control) : -1;
        var toIndex = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next or NavigationDirection.Down => fromIndex + 1,
            NavigationDirection.Previous or NavigationDirection.Up => fromIndex - 1,
            _ => -1,
        };
        if (count == 0 || (fromIndex < 0 && direction is not NavigationDirection.First and not NavigationDirection.Last))
        {
            return null;
        }

        if (toIndex == fromIndex)
        {
            return from;
        }

        if (wrap)
        {
            toIndex = toIndex < 0 ? count - 1 : toIndex >= count ? 0 : toIndex;
        }

        return ScrollIntoView(toIndex);
    }

    private double Guess(object? item) =>
        item is not null && _byType.TryGetValue(item.GetType(), out var kind) && kind.Count > 0
            ? kind.Sum / kind.Count
            : _count > 0 ? _sum / _count : FirstGuess;

    private void Remember(IReadOnlyList<object?> items, int index, double height)
    {
        var item = items[index];
        var kind = item is null ? default : _byType.GetValueOrDefault(item.GetType());
        if (_known[index])
        {
            _sum -= _heights[index];
            _count--;
            kind = (kind.Sum - _heights[index], kind.Count - 1);
        }

        _heights[index] = height;
        _known[index] = true;
        _sum += height;
        _count++;
        if (item is not null)
        {
            _byType[item.GetType()] = (kind.Sum + height, kind.Count + 1);
        }
    }

    private void Unremember(object? item, int index)
    {
        if (!_known[index])
        {
            return;
        }

        _sum -= _heights[index];
        _count--;
        if (item is not null && _byType.TryGetValue(item.GetType(), out var kind))
        {
            _byType[item.GetType()] = (kind.Sum - _heights[index], kind.Count - 1);
        }
    }

    // Every height becomes a guess again, to be measured anew.
    private void ForgetAll()
    {
        for (var i = 0; i < _known.Count; i++)
        {
            _known[i] = false;
        }

        _byType.Clear();
        _sum = 0;
        _count = 0;
    }

    // A change the panel could not follow (a reset, a move) starts the heights over, guessed from the averages so far.
    private void SyncHeights(IReadOnlyList<object?> items)
    {
        if (_heights.Count != items.Count)
        {
            RecycleAll();
            _heights.Clear();
            _known.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                _heights.Add(Guess(items[i]));
                _known.Add(false);
            }
        }
    }

    private void Inserted(int index, System.Collections.IList added)
    {
        var count = added.Count;
        for (var i = 0; i < count; i++)
        {
            _heights.Insert(index + i, Guess(added[i]));
            _known.Insert(index + i, false);
        }

        if (_elements.Count == 0 || index > _first + _elements.Count - 1)
        {
            return;
        }

        if (index <= _first)
        {
            _first += count;
            Renumber(0, count);
        }
        else
        {
            var at = index - _first;
            _elements.InsertRange(at, Enumerable.Repeat<Control?>(null, count));
            Renumber(at + count, count);
        }
    }

    private void Removed(int index, System.Collections.IList removed)
    {
        var count = removed.Count;
        for (var i = 0; i < count; i++)
        {
            Unremember(removed[i], index + i);
        }

        _heights.RemoveRange(index, count);
        _known.RemoveRange(index, count);
        if (_elements.Count == 0 || index > _first + _elements.Count - 1)
        {
            return;
        }

        var from = Math.Max(index, _first) - _first;
        var to = Math.Min(index + count, _first + _elements.Count) - _first;
        for (var i = from; i < to; i++)
        {
            if (_elements[i] is { } element)
            {
                Recycle(element);
            }
        }

        if (to > from)
        {
            _elements.RemoveRange(from, to - from);
        }

        if (index < _first)
        {
            _first = Math.Max(index, _first - count);
        }

        Renumber(from, -count);
    }

    // Tells the containers from the given one on that their items moved by delta.
    private void Renumber(int from, int delta)
    {
        for (var i = from; i < _elements.Count; i++)
        {
            if (_elements[i] is { } element)
            {
                ItemContainerGenerator!.ItemContainerIndexChanged(element, _first + i - delta, _first + i);
            }
        }
    }

    private void MeasureAheadSoon()
    {
        if (!_aheadPosted)
        {
            _aheadPosted = true;
            Dispatcher.UIThread.Post(MeasureAhead, DispatcherPriority.Background);
        }
    }

    // Measures items not yet measured, nearest the built ones first, for a few milliseconds. One above the view moves
    // the view's items by the difference, so the viewport is moved with them (the scroll viewer's anchoring does the
    // same to the offset once they are arranged).
    private void MeasureAhead()
    {
        _aheadPosted = false;
        var items = Items;
        if (VisualRoot is null || ItemContainerGenerator is null || items.Count != _heights.Count || double.IsNaN(_width)
            || _elements.Count == 0)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var childSize = new Size(_width, double.PositiveInfinity);
        var up = _first - 1;
        var down = _first + _elements.Count;
        var shift = 0.0;
        var measured = false;
        IsMeasuringAhead = true;
        try
        {
            while (clock.Elapsed < AheadBudget)
            {
                while (up >= 0 && _known[up])
                {
                    up--;
                }

                while (down < items.Count && _known[down])
                {
                    down++;
                }

                if (up < 0 && down >= items.Count)
                {
                    break;
                }

                // Above first: those are what move the view.
                var index = up >= 0 ? up : down;
                var element = Realize(items, index, [], 0);
                element.Measure(childSize);
                var before = _heights[index];
                Remember(items, index, element.DesiredSize.Height);
                Recycle(element);
                measured = true;
                if (index < _first)
                {
                    shift += _heights[index] - before;
                }
            }
        }
        finally
        {
            IsMeasuringAhead = false;
        }

        if (measured)
        {
            _viewport = _viewport.Translate(new Vector(0, shift));
            _extended = _extended.Translate(new Vector(0, shift));
            InvalidateMeasure();
        }

        if (up >= 0 || down < items.Count)
        {
            MeasureAheadSoon();
        }
    }

    private Control Realize(IReadOnlyList<object?> items, int index, List<Control?> old, int oldFirst)
    {
        var i = index - oldFirst;
        if (i >= 0 && i < old.Count && old[i] is { } built)
        {
            old[i] = null;
            return built;
        }

        var item = items[index];
        var generator = ItemContainerGenerator!;
        _realizingIndex = index;
        try
        {
            if (!generator.NeedsContainer(item, index, out var recycleKey))
            {
                var own = (Control)item!;
                if (!own.IsSet(RecycleKeyProperty))
                {
                    generator.PrepareItemContainer(own, own, index);
                    AddInternalChild(own);
                    own.SetValue(RecycleKeyProperty, ItemIsItsOwnContainer);
                    generator.ItemContainerPrepared(own, item, index);
                }

                own.SetCurrentValue(IsVisibleProperty, true);
                return own;
            }

            if (recycleKey is not null && _recyclePool.TryGetValue(recycleKey, out var pool) && pool.Count > 0)
            {
                var recycled = pool.Pop();
                _realizing = recycled;
                recycled.SetCurrentValue(IsVisibleProperty, true);
                generator.PrepareItemContainer(recycled, item, index);
                AddInternalChild(recycled);
                generator.ItemContainerPrepared(recycled, item, index);
                return recycled;
            }

            var container = generator.CreateContainer(item, index, recycleKey);
            _realizing = container;
            container.SetValue(RecycleKeyProperty, recycleKey);
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
            return container;
        }
        finally
        {
            _realizing = null;
            _realizingIndex = -1;
        }
    }

    private void RecycleRange(List<Control?> elements)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            if (elements[i] is { } element)
            {
                elements[i] = null;
                Recycle(element);
            }
        }
    }

    private void RecycleAll()
    {
        RecycleRange(_elements);
        _elements.Clear();
        _first = 0;
        _startU = 0;
    }

    private void Recycle(Control element)
    {
        _anchorProvider?.UnregisterAnchorCandidate(element);
        var recycleKey = element.GetValue(RecycleKeyProperty);
        if (recycleKey == ItemIsItsOwnContainer)
        {
            element.SetCurrentValue(IsVisibleProperty, false);
            return;
        }

        ItemContainerGenerator!.ClearItemContainer(element);
        RemoveInternalChild(element);
        if (recycleKey is not null)
        {
            element.SetCurrentValue(IsVisibleProperty, false);
            if (!_recyclePool.TryGetValue(recycleKey, out var pool))
            {
                _recyclePool[recycleKey] = pool = new Stack<Control>();
            }

            pool.Push(element);
        }
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
        var resized = !MathUtilities.AreClose(_viewport.Height, viewport.Height);
        _viewport = viewport;
        var buffer = _viewport.Height * CacheLength;
        var top = Math.Max(0, _viewport.Top - buffer);
        var extended = new Rect(0, top, _viewport.Width, Math.Max(0, Math.Min(Bounds.Height, _viewport.Bottom + buffer) - top));

        // Built again once the view comes within half the buffer of either end of what is built (unless that end is
        // the list's own), so the items there are measured before they show.
        var builtBottom = _startU;
        foreach (var element in _elements)
        {
            builtBottom += element?.DesiredSize.Height ?? 0;
        }

        var last = _first + _elements.Count - 1;
        if (resized || _elements.Count == 0
            || (_first > 0 && _viewport.Top - _startU < buffer / 2)
            || (last < Items.Count - 1 && builtBottom - _viewport.Bottom < buffer / 2))
        {
            _extended = extended;
            InvalidateMeasure();
        }
    }
}
