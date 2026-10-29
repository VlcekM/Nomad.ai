using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Nomad.App.Ide;

/// <summary>
/// The Files view's folder tree, drawn by hand: a row per entry whose folders are all expanded, with indent guides,
/// a chevron and an icon. Rows that come and go (a folder expanding or collapsing, files appearing or vanishing on
/// disk) fold their height and fade, and a folder's chevron turns (200 ms, cubic ease-out); a change mid-flight
/// retargets from where it is. Meant to sit in a <see cref="ScrollViewer"/>: only the rows in view are drawn.
/// Clicking a file (or moving to it with the arrow keys) raises <see cref="FileOpened"/> to preview it; a double-click or Enter keeps it.
/// </summary>
public sealed class FileTreeView : Control
{
    public static readonly StyledProperty<FileTree?> TreeProperty =
        AvaloniaProperty.Register<FileTreeView, FileTree?>(nameof(Tree));


    private const double FoldMs = 200;
    private const double HoverMs = 120;
    private const double Indent = 14;
    private const double Inset = 4;
    private const double PadTop = 6;
    private const double Box = 16;
    private const double Slide = 4;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    // Folders are yellow, as in Explorer: a darker back with its tab, a lighter front that tilts open when expanded.
    private static readonly IBrush FolderBack = new SolidColorBrush(Color.FromRgb(0xC9, 0x95, 0x32));
    private static readonly IBrush FolderFront = new SolidColorBrush(Color.FromRgb(0xF0, 0xC1, 0x55));
    private static readonly Geometry FolderBackShape = StreamGeometry.Parse("M1.5,4 C1.5,3.17 2.17,2.5 3,2.5 L6.2,2.5 L7.7,4 L13,4 C13.83,4 14.5,4.67 14.5,5.5 L14.5,12 C14.5,12.83 13.83,13.5 13,13.5 L3,13.5 C2.17,13.5 1.5,12.83 1.5,12 Z");
    private static readonly Geometry FolderFrontShape = StreamGeometry.Parse("M1.5,6.5 L14.5,6.5 L14.5,12 C14.5,12.83 13.83,13.5 13,13.5 L3,13.5 C2.17,13.5 1.5,12.83 1.5,12 Z");
    private static readonly Geometry FolderFrontOpenShape = StreamGeometry.Parse("M3.6,7 L15.2,7 L13.6,12.6 C13.46,13.13 13,13.5 12.45,13.5 L2.2,13.5 C1.75,13.5 1.45,13.08 1.58,12.65 Z");

    private readonly List<Row> _rows = [];
    private FileTree? _tree;
    private Row? _hover;
    private ScrollViewer? _scroller;
    private TopLevel? _top;
    private bool _ticking;
    private bool _restoreScroll;
    private FileNode? _scrolledTo;

    static FileTreeView()
    {
        FocusableProperty.OverrideDefaultValue<FileTreeView>(true);
        AffectsRender<FileTreeView>(TextElement.FontFamilyProperty, TextElement.FontSizeProperty);
        AffectsMeasure<FileTreeView>(TextElement.FontSizeProperty);
    }

    public FileTree? Tree
    {
        get => GetValue(TreeProperty);
        set => SetValue(TreeProperty, value);
    }

    /// <summary>A file to show: its full path, and whether to keep it (a double-click, Enter) or only preview it.</summary>
    public event Action<string, bool>? FileOpened;

    private double FontSize => TextElement.GetFontSize(this);

    private double RowHeight => Math.Round(FontSize * 1.85);

    // ------------------------------------------------------------------ rows

    private struct Anim
    {
        public double Value;
        private double _from;
        private double _to;
        private TimeSpan? _start;

        public bool Running { get; private set; }

        public readonly double Target => _to;

        public void Jump(double value)
        {
            Value = _from = _to = value;
            Running = false;
        }

        public void Retarget(double to)
        {
            if (_to == to && (Running || Value == to))
            {
                return;
            }

            _from = Value;
            _to = to;
            _start = null;
            Running = true;
        }

        public void Step(TimeSpan now, double ms)
        {
            if (!Running)
            {
                return;
            }

            _start ??= now;
            var p = Math.Clamp((now - _start.Value).TotalMilliseconds / ms, 0, 1);
            Value = _from + (_to - _from) * (1 - Math.Pow(1 - p, 3));
            if (p >= 1)
            {
                Value = _to;
                Running = false;
            }
        }
    }

    private sealed class Row(FileNode node)
    {
        public readonly FileNode Node = node;
        public Anim Presence;
        public Anim Hover;
        public Anim Turn;
        public TextLayout? Text;
        public (double Width, double Size, IBrush Brush, FontFamily Family) TextKey;

        public bool Leaving => Presence.Target == 0;
    }

    // ------------------------------------------------------------------ tree

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TreeProperty)
        {
            Attach(change.GetNewValue<FileTree?>());
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += OnScrollChanged;
        }

        _restoreScroll = true;
        ActualThemeVariantChanged += OnResourcesChanged;
        if (Application.Current is { } app)
        {
            app.ResourcesChanged += OnResourcesChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnResourcesChanged;
        if (Application.Current is { } app)
        {
            app.ResourcesChanged -= OnResourcesChanged;
        }

        SaveScroll();
        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
        }

        _scroller = null;
        _top = null;
    }

    private void Attach(FileTree? tree)
    {
        if (_tree == tree)
        {
            return;
        }

        if (_tree is not null)
        {
            SaveScroll();
            _tree.Changed -= OnTreeChanged;
        }

        _tree = tree;
        DropRows(_rows);
        _hover = null;
        if (tree is not null)
        {
            tree.Changed += OnTreeChanged;
            _ = tree.LoadAsync(tree.Root);
        }

        Sync(instant: true);
        _restoreScroll = true;
    }

    private void OnTreeChanged()
    {
        Sync(instant: false);

        // Selected from outside (a file revealed): scroll to it once the rows have unfolded.
        if (_tree?.Selected is { } selected && selected != _scrolledTo && _rows.Find(r => r.Node == selected) is { } row)
        {
            _scrolledTo = selected;
            DispatcherTimer.RunOnce(() => this.BringIntoView(new Rect(0, TopOf(row) - 2, Bounds.Width, RowHeight + 4)),
                TimeSpan.FromMilliseconds(FoldMs + 20));
        }
    }

    private void SaveScroll()
    {
        if (_tree is not null && _scroller is not null)
        {
            _tree.ScrollOffset = _scroller.Offset.Y;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    // A theme or color theme change: the brushes are looked up as the rows are drawn.
    private void OnResourcesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>
    /// Brings the rows in line with the tree: rows for entries no longer shown fold away where they are, rows for new
    /// ones unfold where they belong, and the rest stay (retargeting any that were on their way out).
    /// </summary>
    private void Sync(bool instant)
    {
        // The first rows (the tree just shown) are there at once: the view's own fade brings them in.
        instant |= _rows.Count == 0;
        var desired = _tree?.Visible() ?? [];
        var position = new Dictionary<FileNode, int>(desired.Count);
        for (var i = 0; i < desired.Count; i++)
        {
            position[desired[i]] = i;
        }

        var hasRow = new HashSet<FileNode>(_rows.Select(r => r.Node));
        var merged = new List<Row>(Math.Max(desired.Count, _rows.Count));
        var next = 0;

        void AddNewUpTo(int end)
        {
            for (; next < end; next++)
            {
                if (!hasRow.Contains(desired[next]))
                {
                    var row = new Row(desired[next]);
                    row.Presence.Jump(instant ? 1 : 0);
                    row.Presence.Retarget(1);
                    row.Turn.Jump(desired[next].IsExpanded ? 1 : 0);
                    merged.Add(row);
                }
            }
        }

        foreach (var row in _rows)
        {
            if (position.TryGetValue(row.Node, out var at) && at >= next)
            {
                AddNewUpTo(at);
                next = at + 1;
                Show(row, 1);
            }
            else
            {
                Show(row, 0);
            }

            merged.Add(row);
        }

        AddNewUpTo(desired.Count);

        _rows.Clear();
        foreach (var row in merged)
        {
            if (instant)
            {
                row.Presence.Jump(row.Presence.Target);
                row.Turn.Jump(row.Turn.Target);
            }

            if (!(instant && row.Leaving))
            {
                _rows.Add(row);
            }
        }

        if (_hover is { Leaving: true })
        {
            _hover = null;
        }

        InvalidateMeasure();
        InvalidateVisual();
        Tick();

        void Show(Row row, double presence)
        {
            row.Presence.Retarget(presence);
            row.Turn.Retarget(row.Node.IsExpanded ? 1 : 0);
        }
    }

    // ------------------------------------------------------------------ animation

    private void Tick()
    {
        if (_ticking || _top is null || !_rows.Any(r => r.Presence.Running || r.Hover.Running || r.Turn.Running))
        {
            return;
        }

        _ticking = true;
        _top.RequestAnimationFrame(Step);
    }

    private void Step(TimeSpan now)
    {
        _ticking = false;
        var folding = false;
        foreach (var row in _rows)
        {
            folding |= row.Presence.Running;
            row.Presence.Step(now, FoldMs);
            row.Hover.Step(now, HoverMs);
            row.Turn.Step(now, FoldMs);
        }

        DropRows(_rows.FindAll(r => r.Leaving && !r.Presence.Running));
        if (folding)
        {
            InvalidateMeasure();
        }

        InvalidateVisual();
        Tick();
    }

    private void DropRows(List<Row> rows)
    {
        foreach (var row in rows)
        {
            row.Text?.Dispose();
            row.Text = null;
        }

        if (rows == _rows)
        {
            _rows.Clear();
        }
        else if (rows.Count > 0)
        {
            _rows.RemoveAll(rows.Contains);
        }
    }

    // ------------------------------------------------------------------ layout and drawing

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = PadTop * 2;
        var rowHeight = RowHeight;
        foreach (var row in _rows)
        {
            height += rowHeight * row.Presence.Value;
        }

        return new Size(0, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_restoreScroll && _scroller is not null && _tree is not null)
        {
            _restoreScroll = false;
            var offset = _tree.ScrollOffset;
            Dispatcher.UIThread.Post(() => _scroller?.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, offset)), DispatcherPriority.Loaded);
        }

        return finalSize;
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;

    private Geometry? Shape(string key) =>
        this.TryFindResource(key, out var value) ? value as Geometry : null;

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var rowHeight = RowHeight;
        var (top, bottom) = _scroller is { } s ? (s.Offset.Y, s.Offset.Y + s.Viewport.Height) : (0, Bounds.Height);

        var selectedBrush = Brush("BgSelectedBrush");
        var hoverBrush = Brush("BgHoverBrush");
        var guideBrush = Brush("BorderSubtleBrush");
        var iconPen = new Pen(Brush("TextSecondaryBrush"), 1.35, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var chevronPen = new Pen(Brush("TextMutedBrush"), 1.35, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var guidePen = new Pen(guideBrush, 1);
        var textBrush = Brush("TextPrimaryBrush");
        var chevron = Shape("IconChevronRight");
        var file = Shape("IconFile");
        var family = TextElement.GetFontFamily(this);
        var fontSize = FontSize;
        var selected = _tree?.Selected;

        // Transparent but drawn: the pointer counts over the whole width, not only over the text and icons.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        foreach (var (row, y, clip) in Place())
        {
            if (clip.Bottom < top || clip.Height < 0.5)
            {
                continue;
            }

            if (clip.Top > bottom)
            {
                break;
            }

            using (context.PushClip(new Rect(0, clip.Top, width, clip.Height)))
            using (context.PushOpacity(row.Presence.Value))
            {
                var box = new Rect(Inset, y, Math.Max(0, width - Inset * 2), rowHeight);
                if (row.Node == selected)
                {
                    context.DrawRectangle(selectedBrush, null, box, 6, 6);
                }
                else if (row.Hover.Value > 0.01)
                {
                    using (context.PushOpacity(row.Hover.Value))
                    {
                        context.DrawRectangle(hoverBrush, null, box, 6, 6);
                    }
                }

                var depth = row.Node.Depth;
                for (var d = 0; d < depth; d++)
                {
                    var gx = Math.Round(Left(d) + Box / 2) + 0.5;
                    context.DrawLine(guidePen, new Point(gx, y), new Point(gx, y + rowHeight));
                }

                var x = Left(depth);
                var iconTop = y + (rowHeight - Box) / 2;
                if (row.Node.IsDirectory && chevron is not null)
                {
                    var turn = Matrix.CreateTranslation(-8, -8) * Matrix.CreateRotation(row.Turn.Value * Math.PI / 2)
                        * Matrix.CreateTranslation(x + 8, iconTop + 8);
                    using (context.PushTransform(turn))
                    {
                        context.DrawGeometry(null, chevronPen, chevron);
                    }
                }

                using (context.PushTransform(Matrix.CreateTranslation(x + Box, iconTop)))
                {
                    if (row.Node.IsDirectory)
                    {
                        context.DrawGeometry(FolderBack, null, FolderBackShape);
                        context.DrawGeometry(FolderFront, null, row.Node.IsExpanded ? FolderFrontOpenShape : FolderFrontShape);
                    }
                    else if (file is not null)
                    {
                        context.DrawGeometry(null, iconPen, file);
                    }
                }

                var textX = x + Box * 2 + 6;
                var available = Math.Max(0, box.Right - 8 - textX);
                var key = (available, fontSize, textBrush, family);
                if (row.Text is null || row.TextKey != key)
                {
                    row.Text?.Dispose();
                    row.Text = new TextLayout(row.Node.Name, new Typeface(family), fontSize, textBrush,
                        textWrapping: TextWrapping.NoWrap, textTrimming: TextTrimming.CharacterEllipsis, maxWidth: available);
                    row.TextKey = key;
                }

                row.Text.Draw(context, new Point(textX, y + (rowHeight - row.Text.Height) / 2));
            }
        }
    }

    private static double Left(int depth) => Inset + 4 + depth * Indent;

    /// <summary>
    /// Where each row is drawn, and the band it shows through. A run of neighbouring rows folding together (a
    /// folder's entries) opens like one sheet: the band grows while the rows inside keep their full height and
    /// spacing, sliding down a little as they fade in, rather than each row being squeezed.
    /// </summary>
    private IEnumerable<(Row Row, double Top, Rect Clip)> Place()
    {
        var rowHeight = RowHeight;
        var y = PadTop;
        for (var i = 0; i < _rows.Count;)
        {
            var first = _rows[i];
            var p = first.Presence.Value;
            if (p >= 1)
            {
                yield return (first, y, new Rect(0, y, 0, rowHeight));
                y += rowHeight;
                i++;
                continue;
            }

            var end = i + 1;
            while (end < _rows.Count && Math.Abs(_rows[end].Presence.Value - p) < 1e-9 && _rows[end].Leaving == first.Leaving)
            {
                end++;
            }

            var band = new Rect(0, y, 0, (end - i) * rowHeight * p);
            var slide = Slide * (1 - p);
            for (var k = i; k < end; k++)
            {
                yield return (_rows[k], y + (k - i) * rowHeight - slide, band);
            }

            y += band.Height;
            i = end;
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>The row under a point (not one on its way out).</summary>
    private Row? RowAt(double pointY)
    {
        var rowHeight = RowHeight;
        foreach (var (row, y, clip) in Place())
        {
            if (pointY >= clip.Top && pointY < clip.Bottom && pointY >= y && pointY < y + rowHeight)
            {
                return row.Leaving ? null : row;
            }
        }

        return null;
    }

    private double TopOf(Row target)
    {
        var rowHeight = RowHeight;
        var y = PadTop;
        foreach (var row in _rows)
        {
            if (row == target)
            {
                return y;
            }

            y += rowHeight * row.Presence.Target;
        }

        return y;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHover(RowAt(e.GetPosition(this).Y));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    private void SetHover(Row? row)
    {
        if (row == _hover)
        {
            return;
        }

        _hover?.Hover.Retarget(0);
        _hover = row;
        Cursor = row is null ? null : HandCursor;
        _hover?.Hover.Jump(1); // at once under the pointer; only leaving a row fades
        InvalidateVisual();
        Tick();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || RowAt(e.GetPosition(this).Y) is not { } row)
        {
            return;
        }

        Focus(NavigationMethod.Pointer);

        // The second click of a double-click: a file is kept in its tab; a folder, already toggled once, stays.
        if (e.ClickCount >= 2)
        {
            if (!row.Node.IsDirectory)
            {
                FileOpened?.Invoke(row.Node.FullPath, true);
            }

            e.Handled = true;
            return;
        }

        Select(row);
        if (row.Node.IsDirectory)
        {
            Toggle(row.Node);
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_tree is null || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        var shown = _rows.Where(r => !r.Leaving).ToList();
        if (shown.Count == 0)
        {
            return;
        }

        var index = shown.FindIndex(r => r.Node == _tree.Selected);
        var current = index >= 0 ? shown[index] : null;
        var page = Math.Max(1, (int)((_scroller?.Viewport.Height ?? Bounds.Height) / RowHeight) - 1);

        void MoveTo(int i) => Select(shown[Math.Clamp(i, 0, shown.Count - 1)]);

        switch (e.Key)
        {
            case Key.Down:
                MoveTo(index + 1);
                break;
            case Key.Up:
                MoveTo(index < 0 ? 0 : index - 1);
                break;
            case Key.PageDown:
                MoveTo(index + page);
                break;
            case Key.PageUp:
                MoveTo(index - page);
                break;
            case Key.Home:
                MoveTo(0);
                break;
            case Key.End:
                MoveTo(shown.Count - 1);
                break;
            case Key.Right when current is { Node.IsDirectory: true }:
                if (!current.Node.IsExpanded)
                {
                    Toggle(current.Node);
                }
                else if (index + 1 < shown.Count && shown[index + 1].Node.Parent == current.Node)
                {
                    MoveTo(index + 1);
                }

                break;
            case Key.Left when current is not null:
                if (current.Node is { IsDirectory: true, IsExpanded: true })
                {
                    Toggle(current.Node);
                }
                else if (shown.FindIndex(r => r.Node == current.Node.Parent) is >= 0 and var parent)
                {
                    MoveTo(parent);
                }

                break;
            case Key.Enter or Key.Space when current is not null:
                if (current.Node.IsDirectory)
                {
                    Toggle(current.Node);
                }
                else
                {
                    FileOpened?.Invoke(current.Node.FullPath, true);
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Highlights a row (a file's also opens) and scrolls it into view.</summary>
    private void Select(Row row)
    {
        if (_tree is null)
        {
            return;
        }

        _tree.Selected = _scrolledTo = row.Node;
        if (!row.Node.IsDirectory)
        {
            FileOpened?.Invoke(row.Node.FullPath, false);
        }

        InvalidateVisual();
        this.BringIntoView(new Rect(0, TopOf(row) - 2, Bounds.Width, RowHeight + 4));
    }

    private void Toggle(FileNode folder)
    {
        folder.IsExpanded = !folder.IsExpanded;
        if (folder.IsExpanded && folder.Children is null && _tree is not null)
        {
            _ = _tree.LoadAsync(folder);
        }

        Sync(instant: false);
    }
}
