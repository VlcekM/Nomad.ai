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
using Avalonia.Threading;
using TextMateSharp.Grammars;

namespace Nomad.App.Ide;

/// <summary>
/// What a click on the git margin opens, as Visual Studio's: under the change, the lines as last committed (red) and
/// as they are now (green), in the code's colors. "Show full diff" opens the file's diff with HEAD. Light-dismissed, or Esc.
/// </summary>
internal sealed class CodeGitPeek
{
    private const double MaxListHeight = 320;

    private readonly CodeView _view;
    private readonly Popup _popup;
    private readonly Border _card;
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _fullDiff;
    private readonly StackPanel _lines = new();
    private Action? _showDiff;

    public CodeGitPeek(CodeView view)
    {
        _view = view;
        _fullDiff = new TextBlock { Text = "Show full diff", Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center };
        _fullDiff.PointerPressed += (_, e) =>
        {
            Close();
            _showDiff?.Invoke();
            e.Handled = true;
        };

        var heading = new DockPanel { Margin = new Thickness(4, 0, 4, 6) };
        DockPanel.SetDock(_fullDiff, Dock.Right);
        heading.Children.Add(_fullDiff);
        heading.Children.Add(_title);

        var scroller = new ScrollViewer
        {
            Content = _lines,
            MaxHeight = MaxListHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var body = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        body.Children.Add(heading);
        body.Children.Add(scroller);

        _card = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Margin = CodeView.ShadowRoom,
            BoxShadow = CodeView.CardShadow,
            MinWidth = 420,
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

    /// <summary>Opens under the change (window coordinates), <paramref name="width"/> wide at most.</summary>
    public void Show((Control Target, Rect Rect) anchor, double width, IReadOnlyList<string> removed, IReadOnlyList<string> added, IGrammar? grammar, Action? showDiff)
    {
        _showDiff = showDiff;
        _card.Background = Resource<IBrush>("BgMenuBrush");
        _card.BorderBrush = Resource<IBrush>("BorderCardBrush");
        _card.MaxWidth = Math.Max(_card.MinWidth, width);
        TextElement.SetFontSize(_card, _view.SmallFontSize);
        _fullDiff.Foreground = Resource<IBrush>("AccentBrush");
        _fullDiff.IsVisible = showDiff is not null;
        _title.Text = Title(removed.Count, added.Count);
        _title.Foreground = Resource<IBrush>("TextSecondaryBrush");

        _lines.Children.Clear();
        var indent = Indent([.. removed, .. added]);
        AddLines([.. removed.Select(l => l[Math.Min(indent, l.Length)..])], '-', "DiffDelBgBrush", "DiffDelBrush", grammar);
        AddLines([.. added.Select(l => l[Math.Min(indent, l.Length)..])], '+', "DiffAddBgBrush", "DiffAddBrush", grammar);

        (_popup.PlacementTarget, _popup.PlacementRect) = anchor;
        _popup.HorizontalOffset = -CodeView.ShadowRoom.Left;
        _popup.VerticalOffset = 2 - CodeView.ShadowRoom.Top;
        _popup.IsOpen = true;
        Dispatcher.UIThread.Post(() => _card.Opacity = _popup.IsOpen ? 1 : 0, DispatcherPriority.Loaded);
    }

    public void Close() => _popup.IsOpen = false;

    private static string Title(int removed, int added)
    {
        static string Lines(int n) => n == 1 ? "1 line" : $"{n} lines";

        return (removed, added) switch
        {
            (0, _) => $"{Lines(added)} added since the last commit",
            (_, 0) => $"{Lines(removed)} removed since the last commit",
            _ => $"{Lines(removed)} changed since the last commit",
        };
    }

    // The indentation every line shares (blank ones aside), which the peek leaves out.
    private static int Indent(IReadOnlyList<string> lines)
    {
        var shown = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return shown.Count == 0 ? 0 : shown.Min(l => l.Length - l.TrimStart(' ', '\t').Length);
    }

    // A block of lines on its tint, each with its sign, tokenized in order (a comment or string can run on).
    private void AddLines(IReadOnlyList<string> lines, char sign, string background, string signBrush, IGrammar? grammar)
    {
        IStateStack? state = null;
        foreach (var text in lines)
        {
            CodeSpan[] spans = [];
            if (grammar is not null)
            {
                (spans, state) = Highlighter.Tokenize(grammar, text, state);
            }

            var line = new TextBlock
            {
                FontFamily = _view.FontFamily,
                FontSize = _view.FontSize,
                VerticalAlignment = VerticalAlignment.Center,
            };
            line.Inlines!.Add(new Run($"{sign} ") { Foreground = Resource<IBrush>(signBrush) });
            _view.AddCode(line.Inlines, text.Replace('\t', ' '), spans);
            _lines.Children.Add(new Border
            {
                Background = Resource<IBrush>(background),
                Padding = new Thickness(6, 1, 12, 1),
                Child = line,
            });
        }
    }

    private T? Resource<T>(string key) where T : class =>
        _view.TryFindResource(key, _view.ActualThemeVariant, out var value) ? value as T : null;
}
