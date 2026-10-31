using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Nomad.Core.Lsp;

namespace Nomad.App.Ide;

/// <summary>
/// Ctrl+. in the code, as Visual Studio's light bulb: the fixes for the caret's place (the one to pick first) and the
/// refactorings, in a list under the caret; a group of choices shows as "Group › Choice". Up and Down choose, Enter or a
/// click makes it, Esc or the caret moving closes it. The editor keeps the keyboard and hands it the keys.
/// </summary>
internal sealed class CodeActionMenu
{
    private const double FadeMs = 130;

    private readonly CodeView _view;
    private readonly Popup _popup;
    private readonly Border _card;
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(FadeMs) };
    private readonly List<(LspCodeAction Action, Border Row)> _items = [];
    private int _selected;
    private TextPos _caret;

    public CodeActionMenu(CodeView view, Action<Popup> adopt)
    {
        _view = view;
        _card = new Border
        {
            Child = new ScrollViewer { Content = _rows, MaxHeight = 360, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(3),
            MinWidth = 220,
            MaxWidth = 560,
            Opacity = 0,
            RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(-4px)"),
            Transitions =
            [
                new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(FadeMs), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
                new Avalonia.Animation.TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
            ],
        };
        _popup = new Popup
        {
            Child = _card,
            PlacementTarget = view,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.BottomLeft,
            PlacementGravity = PopupGravity.BottomRight,
            IsLightDismissEnabled = false,
            Focusable = false,
        };
        adopt(_popup);
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (!IsOpen)
            {
                _popup.IsOpen = false;
            }
        };
    }

    public bool IsOpen { get; private set; }

    /// <summary>Shows the actions at the caret (or says there are none).</summary>
    public void Show(IReadOnlyList<LspCodeAction> actions, Rect? under = null)
    {
        _rows.Children.Clear();
        _items.Clear();
        var muted = _view.Theme("TextMutedBrush") ?? _view.Foreground;

        // Fixes first (the one to pick at the top), then the refactorings; a group's choices one by one.
        var fixes = actions.Where(a => a.Kind is null || a.Kind.StartsWith("quickfix", StringComparison.Ordinal)).OrderByDescending(a => a.IsPreferred).ToList();
        var others = actions.Except(fixes).ToList();
        void Add(LspCodeAction action)
        {
            if (action.Nested.Count == 0)
            {
                AddRow(action, null);
                return;
            }

            foreach (var nested in action.Nested)
            {
                AddRow(nested, action.Title);
            }
        }

        foreach (var action in fixes)
        {
            Add(action);
        }

        if (fixes.Count > 0 && others.Count > 0)
        {
            _rows.Children.Add(new Border { Height = 1, Margin = new Thickness(6, 3), Background = _view.Theme("BorderDividerBrush") });
        }

        foreach (var action in others)
        {
            Add(action);
        }

        if (_items.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = "No quick actions here", Foreground = muted, Margin = new Thickness(8, 4) });
        }

        void AddRow(LspCodeAction action, string? group)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            if (group is not null)
            {
                text.Inlines!.Add(new Run(group + "  ›  ") { Foreground = muted });
            }

            text.Inlines!.Add(new Run(action.Title));
            var row = new Border { Child = text, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            var index = _items.Count;
            row.PointerEntered += (_, _) => Select(index);
            row.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                Select(index);
                Choose();
            };
            _items.Add((action, row));
            _rows.Children.Add(row);
        }

        _caret = _view.Doc.Caret;
        _closeTimer.Stop();
        _card.Background = _view.Theme("BgMenuBrush");
        _card.BorderBrush = _view.Theme("BorderCardBrush");
        (_popup.PlacementTarget, _popup.PlacementRect) = _view.WindowAnchor(under ?? _view.NameRect(_caret.Line, _caret.Index));
        _popup.HorizontalOffset = -4;
        _popup.VerticalOffset = 2;
        Select(0);
        IsOpen = true;
        _card.Opacity = 0;
        _card.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(-4px)");
        _popup.IsOpen = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsOpen)
            {
                _card.Opacity = 1;
                _card.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");
            }
        }, DispatcherPriority.Loaded);
    }

    private void Select(int index)
    {
        if (_items.Count == 0)
        {
            return;
        }

        _selected = Math.Clamp(index, 0, _items.Count - 1);
        var selected = _view.Theme("BgSelectedBrush");
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].Row.Background = i == _selected ? selected : Brushes.Transparent;
        }

        _items[_selected].Row.BringIntoView();
    }

    private void Choose()
    {
        if (_items.Count == 0 || _view.Navigator is not { } navigator)
        {
            Close();
            return;
        }

        var action = _items[_selected].Action;
        Close();
        navigator.ApplyCodeAction(action);
    }

    /// <summary>The keys it uses while open (Up, Down, Enter, Tab, Esc); everything else closes it and goes on to the code.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsOpen)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Up when e.KeyModifiers == KeyModifiers.None:
                Select(_selected <= 0 ? _items.Count - 1 : _selected - 1);
                return true;
            case Key.Down when e.KeyModifiers == KeyModifiers.None:
                Select(_selected >= _items.Count - 1 ? 0 : _selected + 1);
                return true;
            case Key.Enter or Key.Tab when e.KeyModifiers == KeyModifiers.None:
                Choose();
                return true;
            case Key.Escape:
                Close();
                return true;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift:
                return false;
            default:
                Close();
                return false;
        }
    }

    /// <summary>The caret moved off where it was opened: it goes.</summary>
    public void Update()
    {
        if (IsOpen && _view.Doc.Caret != _caret)
        {
            Close();
        }
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _card.Opacity = 0;
        _closeTimer.Start();
    }
}
