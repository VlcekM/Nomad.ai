using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Nomad.App.Controls;
using Nomad.Core.Code;

namespace Nomad.App.Ide;

/// <summary>
/// What a code lens opens, as Visual Studio's: under the count, where the declaration is used, grouped by file (each
/// group folds), every line as "41 : the code" with the name marked. A click on a line goes there; the button at the
/// top right moves the list to the Find results pane; "Collapse All" folds every file. Light-dismissed, or Esc.
/// </summary>
internal sealed class CodeLensList
{
    private const double MaxListHeight = 260;
    private const double RowHeight = 22;

    private readonly CodeView _view;
    private readonly Popup _popup;
    private readonly Border _card;
    private readonly StackPanel _groups = new();
    private readonly TextBlock _toggleAll;
    private readonly List<(Control Rows, Icon Chevron)> _folds = [];
    private ICodeNavigator? _navigator;
    private IReadOnlyList<TextHit> _hits = [];
    private string _name = "";
    private CodeKind? _nameKind;
    private int _serial;

    public CodeLensList(CodeView view)
    {
        _view = view;
        _toggleAll = new TextBlock { Text = "Collapse All", Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(8, 6, 0, 2) };
        _toggleAll.PointerPressed += (_, e) =>
        {
            ToggleAll();
            e.Handled = true;
        };

        var dock = new Button
        {
            Theme = Resource<ControlTheme>("IconButton"),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new Icon { Size = 13, Data = Resource<Geometry>("IconPanelRight") },
        };
        ToolTip.SetTip(dock, "Show in the Find results");
        dock.Click += (_, _) =>
        {
            Close();
            _navigator?.ListUses(_name, _hits);
        };

        var scroller = new ScrollViewer { Content = _groups, MaxHeight = MaxListHeight, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_toggleAll, Dock.Bottom);
        DockPanel.SetDock(dock, Dock.Right);
        body.Children.Add(_toggleAll);
        body.Children.Add(dock);
        body.Children.Add(scroller);

        _card = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 6, 6, 6),
            Margin = CodeView.ShadowRoom,
            BoxShadow = CodeView.CardShadow,
            MinWidth = 420,
            MaxWidth = 760,
            Opacity = 0,
            Child = body,
            Transitions =
            [
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(140), Easing = new CubicEaseOut() },
            ],
        };

        _popup = new Popup
        {
            Child = _card,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.BottomLeft,
            PlacementGravity = PopupGravity.BottomRight,
            IsLightDismissEnabled = true,
            Focusable = false,
        };
        _popup.Closed += (_, _) => _card.Opacity = 0;
    }

    /// <summary>The popup, for the view to hold as its logical child (resources and styles reach it so).</summary>
    public Popup Popup => _popup;

    public bool IsOpen => _popup.IsOpen;

    /// <summary>Opens under the lens (window coordinates), "Finding references…" until the places come; a struct's, an interface's or an enum's name is in its color.</summary>
    public async void Show((Control Target, Rect Rect) anchor, string name, CodeKind? nameKind, ICodeNavigator navigator, Task<IReadOnlyList<TextHit>?> uses)
    {
        var serial = ++_serial;
        (_navigator, _name, _nameKind, _hits) = (navigator, name, nameKind, []);
        _card.Background = Resource<IBrush>("BgMenuBrush");
        TextElement.SetFontSize(_card, _view.SmallFontSize);
        _card.BorderBrush = Resource<IBrush>("BorderCardBrush");
        _toggleAll.Foreground = Resource<IBrush>("AccentBrush");
        _toggleAll.IsVisible = false;
        _groups.Children.Clear();
        _groups.Children.Add(Note("Finding references…"));
        (_popup.PlacementTarget, _popup.PlacementRect) = anchor;
        _popup.HorizontalOffset = -CodeView.ShadowRoom.Left;
        _popup.VerticalOffset = 2 - CodeView.ShadowRoom.Top;
        _popup.IsOpen = true;
        Dispatcher.UIThread.Post(() => _card.Opacity = _popup.IsOpen ? 1 : 0, DispatcherPriority.Loaded);

        IReadOnlyList<TextHit>? hits;
        try
        {
            hits = await uses;
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
            hits = null;
        }

        if (serial == _serial && _popup.IsOpen)
        {
            Fill(hits);
        }
    }

    public void Close() => _popup.IsOpen = false;

    private void Fill(IReadOnlyList<TextHit>? hits)
    {
        _groups.Children.Clear();
        _folds.Clear();
        _hits = hits ?? [];
        if (_hits.Count == 0)
        {
            _groups.Children.Add(Note(hits is null ? "The language server didn't answer." : "No references."));
            return;
        }

        foreach (var file in _hits.GroupBy(h => h.Path))
        {
            _groups.Children.Add(Group(file.Key, [.. file]));
        }

        _toggleAll.Text = "Collapse All";
        _toggleAll.IsVisible = true;
    }

    // A file's heading (its chevron folds it) and its lines.
    private Control Group(string path, IReadOnlyList<TextHit> hits)
    {
        var rows = new StackPanel();
        foreach (var hit in hits)
        {
            rows.Children.Add(Row(hit));
        }

        var chevron = new Icon
        {
            Size = 10,
            Data = Resource<Geometry>("IconChevronDown"),
            Stroke = Resource<IBrush>("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var heading = new DockPanel { Height = RowHeight, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        heading.Children.Add(new Border { Width = 18, Child = chevron });
        heading.Children.Add(new TextBlock { Text = $"{Relative(path)} ({hits.Count})", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        heading.PointerPressed += (_, e) =>
        {
            Fold(rows, chevron, rows.IsVisible);
            _toggleAll.Text = _folds.All(f => !f.Rows.IsVisible) ? "Expand All" : "Collapse All";
            e.Handled = true;
        };
        _folds.Add((rows, chevron));
        return new StackPanel { Children = { heading, rows } };
    }

    // "41 : the code" in the code's colors, the name marked as Visual Studio marks it, lit under the pointer; a click goes there.
    private Control Row(TextHit hit)
    {
        var text = hit.Text.TrimStart(' ', '\t');
        var cut = hit.Text.Length - text.Length;
        var start = Math.Clamp(hit.Column - cut, 0, text.Length);
        var end = Math.Clamp(start + hit.Length, start, text.Length);
        // Code as written: the UI font's ligatures ("=>" an arrow) left out.
        var line = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFeatures = [FontFeature.Parse("-calt"), FontFeature.Parse("-liga")],
        };
        line.Inlines!.Add(new Run($"{hit.Line + 1} : ") { Foreground = Resource<IBrush>("TextSecondaryBrush") });
        IEnumerable<CodeSpan> spans = Highlighter.ForFile(hit.Path) is { } grammar ? Highlighter.Tokenize(grammar, text, null).Spans : [];
        if (_nameKind is { } kind)
        {
            spans = [.. spans.Where(s => s.Start + s.Length <= start || s.Start >= end), new CodeSpan(start, end - start, kind)];
        }

        _view.AddCode(line.Inlines, text, spans, start, end, Resource<IBrush>("TextSelectionBrush"));

        var row = new Border
        {
            Height = RowHeight,
            Padding = new Thickness(26, 0, 8, 0),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = line,
            Transitions =
            [
                new BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(120), Easing = new CubicEaseOut() },
            ],
        };
        row.PointerEntered += (_, _) => row.Background = Resource<IBrush>("BgHoverBrush");
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerPressed += (_, e) =>
        {
            Close();
            _navigator?.GoTo(hit);
            e.Handled = true;
        };
        return row;
    }

    private void ToggleAll()
    {
        var collapse = _folds.Any(f => f.Rows.IsVisible);
        foreach (var (rows, chevron) in _folds)
        {
            Fold(rows, chevron, collapse);
        }

        _toggleAll.Text = collapse ? "Expand All" : "Collapse All";
    }

    private void Fold(Control rows, Icon chevron, bool collapse)
    {
        rows.IsVisible = !collapse;
        chevron.Data = Resource<Geometry>(collapse ? "IconChevronRight" : "IconChevronDown");
    }

    private Control Note(string text) => new TextBlock
    {
        Text = text,
        Foreground = Resource<IBrush>("TextMutedBrush"),
        Margin = new Thickness(8, 4),
    };

    private string Relative(string path) =>
        _navigator?.Root is { } root ? Path.GetRelativePath(root, path) : path;

    private T? Resource<T>(string key) where T : class =>
        _view.TryFindResource(key, _view.ActualThemeVariant, out var value) ? value as T : null;
}
