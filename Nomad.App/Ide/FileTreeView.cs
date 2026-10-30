using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nomad.Core.Files;
using Nomad.Core.Git;

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

    public static readonly StyledProperty<bool> ShowAllProperty =
        AvaloniaProperty.Register<FileTreeView, bool>(nameof(ShowAll));

    private const double FoldMs = 200;
    private const double FlashInMs = 140;
    private const double FlashOutMs = 1100;
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
    // Git marks left of a file's icon, as Visual Studio's: a blue lock (committed, unchanged), a red check (changed),
    // a green plus (new). C# files get its green "C#" icon.
    private static readonly IBrush LockBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x97, 0xE3));
    private static readonly IBrush CSharpBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0xB8, 0x5C));
    private static readonly Geometry LockBody = StreamGeometry.Parse("M7,8.6 C7,8.35 7.15,8.2 7.4,8.2 L11.1,8.2 C11.35,8.2 11.5,8.35 11.5,8.6 L11.5,11.6 C11.5,11.85 11.35,12 11.1,12 L7.4,12 C7.15,12 7,11.85 7,11.6 Z");
    private static readonly Geometry LockShackle = StreamGeometry.Parse("M8,8.2 L8,7.1 C8,6.4 8.55,5.85 9.25,5.85 C9.95,5.85 10.5,6.4 10.5,7.1 L10.5,8.2");
    private static readonly Geometry CheckShape = StreamGeometry.Parse("M6.75,9 L8.5,10.75 L11.5,7.25");
    private static readonly Geometry PlusShape = StreamGeometry.Parse("M9.25,6.5 L9.25,11.5 M6.75,9 L11.75,9");
    private static readonly Geometry ProjectShape = StreamGeometry.Parse("M5,8 L5,3.75 C5,3.06 5.56,2.5 6.25,2.5 L14,2.5 C14.69,2.5 15.25,3.06 15.25,3.75 L15.25,11.25 C15.25,11.94 14.69,12.5 14,12.5 L10.75,12.5 M5,5.25 L15.25,5.25");

    private static readonly Geometry FolderFrontOpenShape = StreamGeometry.Parse("M3.6,7 L15.2,7 L13.6,12.6 C13.46,13.13 13,13.5 12.45,13.5 L2.2,13.5 C1.75,13.5 1.45,13.08 1.58,12.65 Z");

    private readonly List<Row> _rows = [];
    private FileTree? _tree;
    private Row? _hover;
    private ScrollViewer? _scroller;
    private TopLevel? _top;
    private bool _ticking;
    private bool _restoreScroll;
    private FileNode? _scrolledTo;

    // The git marks fade in when the folder's state is first read.
    private Anim _gitFade;
    private TextLayout? _csharp;
    private (double Size, FontFamily Family) _csharpKey;

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

    /// <summary>The tree shows every file (<see cref="FileTree.ShowAll"/>), the toolbar's toggle.</summary>
    public bool ShowAll
    {
        get => GetValue(ShowAllProperty);
        set => SetValue(ShowAllProperty, value);
    }

    /// <summary>A file to show: its full path, and whether to keep it (a double-click, Enter) or only preview it.</summary>
    public event Action<string, bool>? FileOpened;

    /// <summary>A changed file's git mark was clicked: its diff with the last commit.</summary>
    public event Action<string>? DiffRequested;

    /// <summary>A right-click on a row (selected first), or on the empty space below them (null: the folder itself).</summary>
    public event Action<FileNode?>? ContextRequested;

    /// <summary>F2 or Delete on the selected row: "rename" or "delete".</summary>
    public event Action<FileNode, string>? ActionRequested;

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

        // The folder a dragged item would go into.
        public Anim Drop;
        public TextLayout? Text;

        // A folder's counts of changed files below it: modified, added, removed.
        public TextLayout?[]? Counts;
        public ((int, int, int) Counts, double Size, IBrush M, IBrush A, IBrush R, FontFamily Family) CountsKey;
        public (double Width, double Size, IBrush Brush, FontFamily Family) TextKey;

        public bool Leaving => Presence.Target == 0;

        // The agent's flash: eases in, then fades out slowly.
        public bool Flashing;
        public TimeSpan? FlashStart;
        public double Flash;
    }

    // ------------------------------------------------------------------ tree

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TreeProperty)
        {
            Attach(change.GetNewValue<FileTree?>());
        }
        else if (change.Property == ShowAllProperty && _tree is not null)
        {
            _tree.ShowAll = ShowAll;
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
            _tree.GitChanged -= OnGitChanged;
            _tree.AgentChanged -= OnAgentChanged;
        }

        _tree = tree;
        DropRows(_rows);
        _hover = null;
        if (tree is not null)
        {
            tree.ShowAll = ShowAll;
            tree.Changed += OnTreeChanged;
            tree.GitChanged += OnGitChanged;
            tree.AgentChanged += OnAgentChanged;
            _gitFade.Jump(tree.Git is null ? 0 : 1);
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

    // The agent changed something: its row flashes, or the lowest row shown above it (a collapsed folder).
    private void OnAgentChanged(string path)
    {
        Row? best = null;
        foreach (var row in _rows)
        {
            if (row.Leaving)
            {
                continue;
            }

            var node = row.Node.FullPath;
            var under = string.Equals(node, path, StringComparison.OrdinalIgnoreCase) ||
                        path.Length > node.Length && path.StartsWith(node, StringComparison.OrdinalIgnoreCase) && path[node.Length] == Path.DirectorySeparatorChar;
            if (under && (best is null || node.Length > best.Node.FullPath.Length))
            {
                best = row;
            }
        }

        if (best is not null)
        {
            best.FlashStart = null; // Started on the next frame.
            best.Flashing = true;
            Tick();
        }

        InvalidateVisual();
    }

    private void OnGitChanged()
    {
        if (_tree?.Git is null)
        {
            _gitFade.Jump(0);
        }
        else
        {
            _gitFade.Retarget(1);
            Tick();
        }

        InvalidateVisual();
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
        if (_ticking || _top is null || !_gitFade.Running && !_rows.Any(r => r.Presence.Running || r.Hover.Running || r.Turn.Running || r.Drop.Running || r.Flashing))
        {
            return;
        }

        _ticking = true;
        _top.RequestAnimationFrame(Step);
    }

    private void Step(TimeSpan now)
    {
        _ticking = false;
        _gitFade.Step(now, FoldMs);
        var folding = false;
        foreach (var row in _rows)
        {
            folding |= row.Presence.Running;
            row.Presence.Step(now, FoldMs);
            row.Hover.Step(now, HoverMs);
            row.Turn.Step(now, FoldMs);
            row.Drop.Step(now, HoverMs);
            if (row.Flashing)
            {
                row.FlashStart ??= now;
                var t = (now - row.FlashStart.Value).TotalMilliseconds;
                row.Flash = t < FlashInMs
                    ? 1 - Math.Pow(1 - t / FlashInMs, 3)
                    : Math.Max(0, 1 - (1 - Math.Pow(1 - Math.Min(1, (t - FlashInMs) / FlashOutMs), 3)));
                if (t >= FlashInMs + FlashOutMs)
                {
                    row.Flashing = false;
                    row.Flash = 0;
                }
            }
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
            foreach (var count in row.Counts ?? [])
            {
                count?.Dispose();
            }

            row.Counts = null;
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
        var textBrush = Brush("TextNavBrush"); // A shade softer than body text, as Solution Explorer's.
        var chevron = Shape("IconChevronRight");
        var file = Shape("IconFile");
        var family = TextElement.GetFontFamily(this);
        var fontSize = FontSize;
        var selected = _tree?.Selected;
        var git = _tree?.Git;
        var gitFade = _gitFade.Value;
        var checkPen = new Pen(Brush("DiffDelBrush"), 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var plusPen = new Pen(Brush("DiffAddBrush"), 1.4, lineCap: PenLineCap.Round);
        var shacklePen = new Pen(LockBrush, 1.15, lineCap: PenLineCap.Round);
        var showAll = _tree?.ShowAll == true;
        var accentBrush = Brush("AccentBrush");

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

                // The agent just changed it (or something in it): an accent wash that fades.
                if (row.Flash > 0.01)
                {
                    using (context.PushOpacity(row.Flash * 0.38))
                    {
                        context.DrawRectangle(accentBrush, null, box, 6, 6);
                    }
                }
                else if (row.Hover.Value > 0.01)
                {
                    using (context.PushOpacity(row.Hover.Value))
                    {
                        context.DrawRectangle(hoverBrush, null, box, 6, 6);
                    }
                }

                // Where a dragged item would go: an accent outline round the folder, with a faint wash.
                if (row.Drop.Value > 0.01)
                {
                    using (context.PushOpacity(row.Drop.Value))
                    {
                        using (context.PushOpacity(0.14))
                        {
                            context.DrawRectangle(accentBrush, null, box, 6, 6);
                        }

                        context.DrawRectangle(null, new Pen(accentBrush, 1.2), box.Deflate(0.6), 6, 6);
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

                // Items shown only with "Show all files" (build output, a project's own file) have their icon faded.
                using (context.PushOpacity(showAll && FileTree.IsHidden(row.Node) ? HiddenOpacity : 1))
                using (context.PushTransform(Matrix.CreateTranslation(x + Box, iconTop)))
                {
                    if (row.Node.Project is not null)
                    {
                        // A project, as Solution Explorer's: a window, open at its lower left for its badge: a globe
                        // for a web app, a flask for tests, else a green "C#".
                        context.DrawGeometry(null, iconPen, ProjectShape);
                        switch (row.Node.ProjectKind)
                        {
                            case ProjectKind.Web:
                                context.DrawGeometry(null, WebPen, GlobeShape);
                                break;
                            case ProjectKind.Test:
                                context.DrawGeometry(TestFill, null, FlaskLiquid);
                                context.DrawGeometry(null, TestPen, FlaskShape);
                                break;
                            default:
                                var mark = ProjectMark(family);
                                mark.Draw(context, new Point(-0.5, Box - mark.Height + 1.5));
                                break;
                        }
                    }
                    else if (row.Node.IsDirectory)
                    {
                        context.DrawGeometry(FolderBack, null, FolderBackShape);
                        context.DrawGeometry(FolderFront, null, row.Node.IsExpanded ? FolderFrontOpenShape : FolderFrontShape);
                    }
                    else if (row.Node.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        var csharp = CSharpIcon(family);
                        csharp.Draw(context, new Point((Box - csharp.WidthIncludingTrailingWhitespace) / 2, (Box - csharp.Height) / 2 + 0.5));
                    }
                    else if (IsSolution(row.Node.Name))
                    {
                        // A solution (.sln, .slnx): Visual Studio's logo, as its own icon for them.
                        context.DrawGeometry(SolutionBrush, null, SolutionShape);
                    }
                    else if (row.Node.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        // Markdown: its "M" and down arrow in a frame.
                        context.DrawGeometry(null, MarkdownPen, MarkdownShape);
                    }
                    else if (row.Node.Name.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        // .gitignore, .gitattributes, .gitmodules: git's orange diamond with its branch.
                        context.DrawGeometry(GitBrush, null, GitShape);
                        context.DrawGeometry(null, GitBranchPen, GitBranch);
                        context.DrawGeometry(GitDark, null, GitNodes);
                    }
                    else if (file is not null)
                    {
                        context.DrawGeometry(null, iconPen, file);
                    }
                }

                // A file's git mark where a folder's chevron is.
                if (!row.Node.IsDirectory && git is not null && gitFade > 0.01)
                {
                    using (context.PushOpacity(gitFade))
                    using (context.PushTransform(Matrix.CreateTranslation(x, iconTop)))
                    {
                        switch (git.StateOf(row.Node.FullPath))
                        {
                            case GitFileState.Clean:
                                context.DrawGeometry(LockBrush, null, LockBody);
                                context.DrawGeometry(null, shacklePen, LockShackle);
                                break;
                            case GitFileState.Modified:
                                context.DrawGeometry(null, checkPen, CheckShape);
                                break;
                            case GitFileState.Added:
                                context.DrawGeometry(null, plusPen, PlusShape);
                                break;
                        }
                    }
                }

                var textX = x + Box * 2 + 6;
                var available = Math.Max(0, box.Right - 8 - textX);

                // A folder's changes below it, after its name: modified (as the title bar's "N changed"), added, removed.
                var counts = row.Node.IsDirectory && git is not null ? Counts(row, git.CountsUnder(row.Node.FullPath), fontSize) : null;
                var countsWidth = 0.0;
                foreach (var count in counts ?? [])
                {
                    countsWidth += count is null ? 0 : count.WidthIncludingTrailingWhitespace + CountGap;
                }

                available = Math.Max(0, available - countsWidth);
                // Changed by the agent this session: its name in the accent color (a folder's when anything in it was).
                var nameBrush = _tree?.IsAgentTouched(row.Node) == true ? accentBrush : textBrush;
                var key = (available, fontSize, nameBrush, family);
                if (row.Text is null || row.TextKey != key)
                {
                    row.Text?.Dispose();
                    row.Text = new TextLayout(row.Node.Name, new Typeface(family), fontSize, nameBrush,
                        textWrapping: TextWrapping.NoWrap, textTrimming: TextTrimming.CharacterEllipsis, maxWidth: available);
                    row.TextKey = key;
                }

                row.Text.Draw(context, new Point(textX, y + (rowHeight - row.Text.Height) / 2));
                if (countsWidth > 0 && gitFade > 0.01)
                {
                    using (context.PushOpacity(gitFade))
                    {
                        var cx = box.Right - 8 - countsWidth;
                        foreach (var count in counts!)
                        {
                            if (count is not null)
                            {
                                cx += CountGap;
                                count.Draw(context, new Point(cx, y + (rowHeight - count.Height) / 2));
                                cx += count.WidthIncludingTrailingWhitespace;
                            }
                        }
                    }
                }
            }
        }
    }

    private static double Left(int depth) => Inset + 4 + depth * Indent;

    private const double CountGap = 7;

    /// <summary>A folder row's counts, laid out (none for a count of zero); null when nothing below it changed.</summary>
    private TextLayout?[]? Counts(Row row, (int Modified, int Added, int Removed) counts, double fontSize)
    {
        if (counts == default)
        {
            return null;
        }

        var size = fontSize - 1.5;
        var family = this.TryFindResource("MonoFont", out var mono) && mono is FontFamily m ? m : FontFamily.Default;
        var key = (counts, size, Brush("WaitingBrush"), Brush("DiffAddBrush"), Brush("DiffDelBrush"), family);
        if (row.Counts is null || row.CountsKey != key)
        {
            foreach (var old in row.Counts ?? [])
            {
                old?.Dispose();
            }

            var typeface = new Typeface(family);
            TextLayout? Make(int n, string sign, IBrush brush) =>
                n == 0 ? null : new TextLayout(sign + n.ToString(System.Globalization.CultureInfo.InvariantCulture), typeface, size, brush);

            row.Counts = [Make(counts.Modified, "", key.Item3), Make(counts.Added, "+", key.Item4), Make(counts.Removed, "−", key.Item5)];
            row.CountsKey = key;
        }

        return row.Counts;
    }

    private const double HiddenOpacity = 0.45;

    private static bool IsSolution(string name) =>
        name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    // A web project's globe and a test project's flask, at the project window's lower left.
    private static readonly Pen WebPen = new(new SolidColorBrush(Color.FromRgb(0x3C, 0x9C, 0xDC)), 1.1);
    private static readonly Geometry GlobeShape = StreamGeometry.Parse(
        "M8,11.75 A3.75,3.75 0 1 1 0.5,11.75 A3.75,3.75 0 1 1 8,11.75 Z M0.5,11.75 L8,11.75 M4.25,8 C5.4,9.1 5.9,10.4 5.9,11.75 C5.9,13.1 5.4,14.4 4.25,15.5 C3.1,14.4 2.6,13.1 2.6,11.75 C2.6,10.4 3.1,9.1 4.25,8 Z");
    private static readonly Pen TestPen = new(new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)), 1.1, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IBrush TestFill = new SolidColorBrush(Color.FromArgb(0x90, 0x4E, 0xC9, 0xB0));
    private static readonly Geometry FlaskShape = StreamGeometry.Parse(
        "M2.25,8 L6.25,8 M3,8 L3,10.6 L0.9,14.3 C0.7,14.7 0.95,15.25 1.45,15.25 L7.05,15.25 C7.55,15.25 7.8,14.7 7.6,14.3 L5.5,10.6 L5.5,8");
    private static readonly Geometry FlaskLiquid = StreamGeometry.Parse("M1.9,12.6 L6.6,12.6 L7.6,14.3 C7.8,14.7 7.55,15.25 7.05,15.25 L1.45,15.25 C0.95,15.25 0.7,14.7 0.9,14.3 Z");

    // Solutions: Visual Studio's logo in its purple.
    private static readonly IBrush SolutionBrush = new SolidColorBrush(Color.FromRgb(0x9B, 0x7B, 0xDA));
    private static readonly Geometry SolutionShape = StreamGeometry.Parse(
        "F1 M11.2,1.2 L14.8,2.9 L14.8,13.1 L11.2,14.8 L4.9,9.1 L2.4,11 L1.2,10.4 L1.2,5.6 L2.4,5 L4.9,6.9 Z M11.2,5.3 L7.8,8 L11.2,10.7 Z M2.6,6.8 L2.6,9.2 L3.9,8 Z");

    // Markdown: VS Code's blue.
    private static readonly Pen MarkdownPen = new(new SolidColorBrush(Color.FromRgb(0x51, 0x9A, 0xBA)), 1.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly Geometry MarkdownShape = StreamGeometry.Parse(
        "M1.5,4.5 C1.5,3.95 1.95,3.5 2.5,3.5 L13.5,3.5 C14.05,3.5 14.5,3.95 14.5,4.5 L14.5,11.5 C14.5,12.05 14.05,12.5 13.5,12.5 L2.5,12.5 C1.95,12.5 1.5,12.05 1.5,11.5 Z M3.75,10.25 L3.75,5.75 L5.75,8 L7.75,5.75 L7.75,10.25 M11,5.75 L11,10 M9.4,8.5 L11,10.1 L12.6,8.5");

    // Git's logo: an orange diamond with a branch on it.
    private static readonly IBrush GitBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x50, 0x33));
    private static readonly IBrush GitDark = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly Pen GitBranchPen = new(GitDark, 1.15, lineCap: PenLineCap.Round);
    private static readonly Geometry GitShape = StreamGeometry.Parse(
        "M7.1,1.4 C7.6,0.9 8.4,0.9 8.9,1.4 L14.6,7.1 C15.1,7.6 15.1,8.4 14.6,8.9 L8.9,14.6 C8.4,15.1 7.6,15.1 7.1,14.6 L1.4,8.9 C0.9,8.4 0.9,7.6 1.4,7.1 Z");
    private static readonly Geometry GitBranch = StreamGeometry.Parse("M5.4,4.6 L7.6,6.8 L7.6,10.6 M7.6,6.8 L10.4,7.6");
    private static readonly Geometry GitNodes = StreamGeometry.Parse(
        "M8.75,10.9 A1.15,1.15 0 1 1 6.45,10.9 A1.15,1.15 0 1 1 8.75,10.9 Z M11.55,7.7 A1.15,1.15 0 1 1 9.25,7.7 A1.15,1.15 0 1 1 11.55,7.7 Z");

    private TextLayout? _projectMark;
    private FontFamily? _projectMarkFamily;

    /// <summary>The project icon's "C#", in the C# file icon's green.</summary>
    private TextLayout ProjectMark(FontFamily family)
    {
        if (_projectMark is null || _projectMarkFamily != family)
        {
            _projectMark?.Dispose();
            _projectMark = new TextLayout("C#", new Typeface(family, weight: FontWeight.Bold), 8.5, CSharpBrush);
            _projectMarkFamily = family;
        }

        return _projectMark;
    }

    /// <summary>Visual Studio's C# file icon: "C#" in green.</summary>
    private TextLayout CSharpIcon(FontFamily family)
    {
        const double size = 11;
        if (_csharp is null || _csharpKey != (size, family))
        {
            _csharp?.Dispose();
            _csharp = new TextLayout("C#", new Typeface(family, weight: FontWeight.Bold), size, CSharpBrush);
            _csharpKey = (size, family);
        }

        return _csharp;
    }

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

    // ------------------------------------------------------------------ drag

    // The row pressed (and where), until the press ends; dragged once the pointer has moved far enough.
    private (Row Row, Point At)? _press;
    private bool _dragging;
    private FileNode? _dropTarget;

    /// <summary>A file or folder dragged out of the tree (each move, then with true once let go, or cancelled with null).</summary>
    public event Action<string, PointerEventArgs?, bool>? FileDragged;

    /// <summary>A file or folder dropped on a folder of the tree: its path, and the folder's.</summary>
    public event Action<string, string>? MoveRequested;

    // The folder a drop at this point goes into: a folder row's own, a file's parent, the root below the rows; none
    // outside the tree, or where the item already is (or inside itself).
    private FileNode? DropTargetAt(Point at, FileNode dragged)
    {
        if (_tree is null || !new Rect(Bounds.Size).Contains(at))
        {
            return null;
        }

        var target = RowAt(at.Y)?.Node is { } node ? (node.IsDirectory ? node : node.Parent) : _tree.Root;
        return target is null || target == dragged.Parent || target == dragged || dragged.IsAncestorOf(target) ? null : target;
    }

    private void SetDropTarget(FileNode? target)
    {
        if (target == _dropTarget)
        {
            return;
        }

        _dropTarget = target;
        foreach (var row in _rows)
        {
            row.Drop.Retarget(row.Node == target ? 1 : 0);
        }

        Tick();
    }

    private void EndDrag(PointerEventArgs? e, bool drop)
    {
        if (!_dragging || _press is not { } press)
        {
            _press = null;
            return;
        }

        _dragging = false;
        _press = null;
        var target = _dropTarget;
        SetDropTarget(null);
        Cursor = _hover is null ? null : HandCursor;
        if (drop && target is not null)
        {
            MoveRequested?.Invoke(press.Row.Node.FullPath, target.FullPath);
            FileDragged?.Invoke(press.Row.Node.FullPath, null, false);
        }
        else
        {
            FileDragged?.Invoke(press.Row.Node.FullPath, drop ? e : null, drop);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag(null, drop: false);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (_press is { } press)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndDrag(null, drop: false);
            }
            else if (_dragging || Math.Abs(at.X - press.At.X) >= 6 || Math.Abs(at.Y - press.At.Y) >= 6)
            {
                if (!_dragging)
                {
                    _dragging = true;
                    e.Pointer.Capture(this);
                    SetHover(null);
                    Cursor = new Cursor(StandardCursorType.DragMove);
                }

                SetDropTarget(DropTargetAt(at, press.Row.Node));
                FileDragged?.Invoke(press.Row.Node.FullPath, e, false);
                e.Handled = true;
                return;
            }
        }

        SetHover(RowAt(at.Y));
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

        // A right-click selects the row (without opening it); its menu opens on the release.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            Focus(NavigationMethod.Pointer);
            if (RowAt(e.GetPosition(this).Y) is { } clicked)
            {
                Select(clicked, open: false);
            }

            e.Handled = true;
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || RowAt(e.GetPosition(this).Y) is not { } row)
        {
            return;
        }

        Focus(NavigationMethod.Pointer);

        // A changed file's red check (or green plus): its diff with the last commit.
        var x = e.GetPosition(this).X;
        if (!row.Node.IsDirectory && _tree?.Git is { } git && x >= Left(row.Node.Depth) && x < Left(row.Node.Depth) + Box &&
            git.StateOf(row.Node.FullPath) == GitFileState.Modified)
        {
            Select(row);
            DiffRequested?.Invoke(row.Node.FullPath);
            e.Handled = true;
            return;
        }

        // The second click of a double-click: a file is kept in its tab; a folder, already toggled once, stays.
        if (e.ClickCount >= 2)
        {
            if (!row.Node.IsDirectory)
            {
                FileOpened?.Invoke(row.Node.FullPath, true);
            }
            else if (row.Node.Project is { } project)
            {
                // As in Solution Explorer: a project's double-click opens its project file (and leaves it as it was).
                Toggle(row.Node);
                FileOpened?.Invoke(project, true);
            }

            e.Handled = true;
            return;
        }

        Select(row);
        if (row.Node.IsDirectory)
        {
            Toggle(row.Node);
        }

        _press = (row, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left && _dragging)
        {
            EndDrag(e, drop: true);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        _press = null;
        if (e.InitialPressMouseButton == MouseButton.Right && _tree is not null)
        {
            ContextRequested?.Invoke(RowAt(e.GetPosition(this).Y)?.Node);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_tree is null || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        // F2 renames the selected item, Delete deletes it (each asks first); the Menu key opens its menu.
        if (e.Key is Key.F2 or Key.Delete or Key.Apps && _tree.Selected is { } selected && selected != _tree.Root)
        {
            if (e.Key == Key.Apps)
            {
                ContextRequested?.Invoke(selected);
            }
            else
            {
                ActionRequested?.Invoke(selected, e.Key == Key.F2 ? "rename" : "delete");
            }

            e.Handled = true;
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
    private void Select(Row row, bool open = true)
    {
        if (_tree is null)
        {
            return;
        }

        _tree.Selected = _scrolledTo = row.Node;
        if (!row.Node.IsDirectory && open)
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
