using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Utilities;
using Nomad.Core.Code;
using Nomad.Core.Lsp;

namespace Nomad.App.Ide;

/// <summary>
/// The code's completion list, as Visual Studio's IntelliSense: it opens on a '.', on starting to type a name, or on
/// Ctrl+Space, with what the language server says fits there, narrowed as the name is typed (what starts it, then its
/// capitals, then what has it inside). Up and Down choose, Tab or Enter (or a '(' '.' ';' after some typing) put it in,
/// with the using it needs; Esc, or the caret leaving the name, closes it.
/// </summary>
internal sealed class CodeCompletion
{
    private const double FadeMs = 140;

    private readonly CodeView _view;
    private readonly Popup _popup;
    private readonly Border _card;
    private readonly CompletionList _list;
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(FadeMs) };
    private readonly DispatcherTimer _detailTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private CancellationTokenSource? _cts;
    private IReadOnlyList<LspCompletion> _items = [];
    private bool _incomplete;
    private bool _waiting;
    private bool _reasked;

    // What the server said of the completions chosen so far (their usings), for putting them in at once.
    private readonly Dictionary<LspCompletion, LspResolvedCompletion> _resolved = [];
    private string _askedTyped = "";

    // The name being completed: its line and where it starts (it runs to the caret).
    private int _line;
    private int _start;

    public CodeCompletion(CodeView view, Action<Popup> adopt)
    {
        _view = view;
        _list = new CompletionList(view);
        _list.Committed += () => Commit(null);
        _list.SelectionChanged += () =>
        {
            _detailTimer.Stop();
            _detailTimer.Start();
        };
        _card = new Border
        {
            Child = _list,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(3),
            Opacity = 0,
            RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(-4px)"),
            Transitions =
            [
                new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(FadeMs), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
                new Avalonia.Animation.TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
            ],
            Focusable = false,
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
        _detailTimer.Tick += (_, _) =>
        {
            _detailTimer.Stop();
            _ = ShowDetailAsync();
        };
    }

    /// <summary>Showing (or asked for and waiting): it takes the keys it uses.</summary>
    public bool IsOpen { get; private set; }

    // Showing a list now (not only waiting for one).
    private bool IsShown => IsOpen && _list.Count > 0;

    /// <summary>Asks for completions at the caret (after a '.', or a name begun; Ctrl+Space with no trigger).</summary>
    public void Start(string? trigger, bool explicitly = false)
    {
        var doc = _view.Doc;
        var caret = doc.Caret;
        if (_view.Navigator is not { } navigator || doc.IsReadOnly || _view.InCommentOrString(caret))
        {
            return;
        }

        var text = doc.Lines[caret.Line];
        var start = Math.Min(caret.Index, text.Length);
        while (start > 0 && IsWord(text[start - 1]))
        {
            start--;
        }

        // Not inside a number.
        if (!explicitly && start < caret.Index && char.IsDigit(text[start]))
        {
            return;
        }

        _line = caret.Line;
        _start = start;
        IsOpen = true;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _askedTyped = text[start..caret.Index];
        _reasked = false;
        _ = AskAsync(navigator, caret, trigger, explicitly, cts.Token);
    }

    private async Task AskAsync(ICodeNavigator navigator, TextPos caret, string? trigger, bool explicitly, CancellationToken cancellationToken)
    {
        _waiting = true;
        LspCompletionList? found = null;

        // The server drops a question when the text changes under it (typing on): asked again where the caret is now.
        // Asked again because nothing fitted: the server may still be gathering the types not imported, so an empty
        // answer is tried again a little later (while the name is still being typed).
        var again = _reasked;
        for (var attempt = 0; attempt < (again ? 5 : 3); attempt++)
        {
            try
            {
                found = await navigator.CompleteAsync(caret.Line, caret.Index, trigger, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested || !IsOpen)
            {
                return;
            }

            if (found is { Items.Count: > 0 } || found is not null && !again)
            {
                break;
            }

            await Task.Delay(found is null ? 60 : 700, CancellationToken.None);
            if (cancellationToken.IsCancellationRequested || !IsOpen || _view.Doc.Caret.Line != _line)
            {
                return;
            }

            trigger = _view.Doc.Caret == caret ? trigger : null;
            caret = _view.Doc.Caret;
        }

        _waiting = false;
        if (found is null || found.Items.Count == 0)
        {
            Close();
            return;
        }

        _items = found.Items;
        _incomplete = found.IsIncomplete;
        Refilter(explicitly);
    }

    /// <summary>The caret moved or the text changed: narrow the list to the name as typed now, or close when off it.</summary>
    public void Update()
    {
        if (!IsOpen)
        {
            return;
        }

        var doc = _view.Doc;
        var caret = doc.Caret;
        if (caret.Line != _line || caret.Index < _start || _line >= doc.Lines.Count)
        {
            Close();
            return;
        }

        var text = doc.Lines[_line];
        if (caret.Index > text.Length || text[_start..caret.Index].Any(c => !IsWord(c)))
        {
            Close();
            return;
        }

        // Still waiting for the first answer: it is narrowed when it comes.
        if (_waiting && _items.Count == 0)
        {
            return;
        }

        // Typing past what the server knew of (it said the list was partial): asked again.
        var typed = text[_start..caret.Index];
        if (_incomplete && typed != _askedTyped && _view.Navigator is { } navigator)
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            _askedTyped = typed;
            _ = AskAsync(navigator, caret, null, false, cts.Token);
        }

        Refilter(false);
    }

    private void Refilter(bool explicitly)
    {
        var doc = _view.Doc;
        var caret = doc.Caret;
        if (!IsOpen || caret.Line != _line || caret.Index < _start)
        {
            Close();
            return;
        }

        var typed = doc.Lines[_line][_start..caret.Index];
        var ranked = CompletionFilter.Rank(_items, typed);
        if (ranked.Count == 0)
        {
            // Nothing fits: asked once more (types from namespaces not imported come once the server has gathered
            // them, after the first question), then out of the way.
            if (!_reasked && typed.Length > 0 && _view.Navigator is { } navigator)
            {
                _reasked = true;
                _items = [];
                _cts?.Cancel();
                var cts = _cts = new CancellationTokenSource();
                _askedTyped = typed;
                _list.SetItems([], -1);
                _card.Opacity = 0;
                _ = AskAsync(navigator, caret, null, explicitly, cts.Token);
                return;
            }

            Close();
            return;
        }

        // Only the name typed in full, nothing more to offer: no need for the list (unless asked for).
        if (!explicitly && ranked.Count == 1 && ranked[0].Item.FilterText == typed && typed.Length > 0)
        {
            Close();
            return;
        }

        _list.SetItems(ranked, CompletionFilter.Best(ranked, typed));
        Show();
    }

    private void Show()
    {
        _closeTimer.Stop();
        _card.Background = _view.Theme("BgMenuBrush");
        _card.BorderBrush = _view.Theme("BorderCardBrush");
        (_popup.PlacementTarget, _popup.PlacementRect) = _view.WindowAnchor(_view.NameRect(_line, _start));
        _popup.HorizontalOffset = -CompletionList.TextInset - _card.Padding.Left - 1;
        if (!_popup.IsOpen)
        {
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
        else
        {
            _card.Opacity = 1;
            _card.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");
        }
    }

    /// <summary>The text scrolled: the list follows its name.</summary>
    public void Reposition()
    {
        if (IsShown && _popup.IsOpen)
        {
            (_popup.PlacementTarget, _popup.PlacementRect) = _view.WindowAnchor(_view.NameRect(_line, _start));
        }
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _cts?.Cancel();
        _waiting = false;
        _detailTimer.Stop();
        _items = [];
        _resolved.Clear();
        if (_popup.IsOpen)
        {
            _card.Opacity = 0;
            _closeTimer.Start();
        }
    }

    /// <summary>A key while it's open: the ones it uses (moving through it, putting it in, closing) it takes.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsOpen)
        {
            return false;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            return true;
        }

        if (!IsShown || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Up:
                _list.Move(-1);
                return true;
            case Key.Down:
                _list.Move(1);
                return true;
            case Key.PageUp:
                _list.Move(-CompletionList.Visible + 1);
                return true;
            case Key.PageDown:
                _list.Move(CompletionList.Visible - 1);
                return true;
            case Key.Tab or Key.Enter when e.KeyModifiers == KeyModifiers.None:
                Commit(null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A character is about to be typed while it is open: a '(' '.' ';' ',' after some of the name puts the chosen one
    /// in first (and the character after it); anything else that isn't part of a name closes it.
    /// </summary>
    public void BeforeTyping(string text)
    {
        if (!IsOpen || text.Length == 0 || text.All(IsWord))
        {
            return;
        }

        var doc = _view.Doc;
        var typed = doc.Caret.Line == _line && doc.Caret.Index >= _start ? doc.Lines[_line][_start..doc.Caret.Index] : "";
        if (IsShown && typed.Length > 0 && text is "(" or "." or ";" or "," or "[" or ")" or "]")
        {
            Commit(null);
        }

        Close();
    }

    /// <summary>A character was typed: a '.' or a name begun asks for completions.</summary>
    public void AfterTyping(string text)
    {
        if (text == ".")
        {
            Start(".");
            return;
        }

        if (IsOpen || text.Length != 1 || !IsWord(text[0]) || char.IsDigit(text[0]))
        {
            return;
        }

        // The first letter of a name (not one typed into the middle of a word).
        var doc = _view.Doc;
        var caret = doc.Caret;
        var line = doc.Lines[caret.Line];
        var before = caret.Index - 2;
        if ((before < 0 || !IsWord(line[before])) && (caret.Index >= line.Length || !IsWord(line[caret.Index])))
        {
            Start(null);
        }
    }

    // The chosen completion goes in for the name typed so far, with the usings and such it needs as the same undo step
    // when they are known already (the chosen one is asked about as it is chosen); else they follow as a step of their own.
    private void Commit(LspCompletion? item)
    {
        item ??= _list.Selected;
        var doc = _view.Doc;
        if (item is null || doc.Caret.Line != _line)
        {
            Close();
            return;
        }

        var caret = doc.Caret;
        var line = doc.Lines[_line];
        var end = caret.Index;
        while (end < line.Length && IsWord(line[end]))
        {
            end++; // The rest of the name, as Visual Studio replaces.
        }

        // From where the server says the completion goes (it may take in a character before the name), else the name.
        var start = item.Edit is { } edit && edit.Line == _line && edit.Column <= caret.Index ? edit.Column : _start;
        var resolved = _resolved.GetValueOrDefault(item);
        Close();
        doc.BeginGroup();
        doc.Replace(new TextPos(_line, start), new TextPos(_line, end), item.InsertText);
        if (resolved is { AdditionalEdits.Count: > 0 })
        {
            doc.ApplyEdits(resolved.AdditionalEdits, group: false);
        }

        doc.EndGroup();
        _view.Committed();
        if (resolved is null && _view.Navigator is { } navigator)
        {
            _ = AddUsingsAsync(navigator, item, doc);
        }
    }

    private static async Task AddUsingsAsync(ICodeNavigator navigator, LspCompletion item, CodeDocument doc)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (await navigator.ResolveCompletionAsync(item, timeout.Token) is { AdditionalEdits.Count: > 0 } resolved)
            {
                doc.ApplyEdits(resolved.AdditionalEdits);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The chosen one's declaration under the list, as Visual Studio's description beside it.
    private async Task ShowDetailAsync()
    {
        if (!IsShown || _list.Selected is not { } item || _view.Navigator is not { } navigator)
        {
            return;
        }

        try
        {
            var resolved = await navigator.ResolveCompletionAsync(item, _cts?.Token ?? default);
            if (resolved is not null)
            {
                _resolved[item] = resolved;
            }

            if (_list.Selected == item)
            {
                var signature = HoverText.Parse(resolved?.Documentation) is { } parsed ? parsed.Info.Signature : resolved?.Detail ?? item.Detail;
                _list.Detail = signature;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The protocol's CompletionItemKind as the code's symbol kinds (null: a keyword or snippet, no icon).</summary>
    public static SymbolKind? KindOf(int kind) => kind switch
    {
        2 => SymbolKind.Method,
        3 => SymbolKind.Function,
        4 => SymbolKind.Constructor,
        5 => SymbolKind.Field,
        6 => SymbolKind.Variable,
        7 => SymbolKind.Class,
        8 => SymbolKind.Interface,
        9 => SymbolKind.File,
        10 => SymbolKind.Property,
        13 => SymbolKind.Enum,
        20 => SymbolKind.EnumMember,
        21 => SymbolKind.Constant,
        22 => SymbolKind.Struct,
        23 => SymbolKind.Event,
        25 => SymbolKind.Variable,
        _ => null,
    };
}

/// <summary>The completion list's rows, drawn: each completion's icon and name (the typed part bold), the chosen one picked out, its declaration below.</summary>
internal sealed class CompletionList : Control
{
    public const int Visible = 10;
    public const double TextInset = 26;
    private const double RowHeight = 22;
    private const double Width0 = 380;

    private readonly CodeView _view;
    private List<CompletionFilter.Ranked> _rows = [];
    private int _selected;
    private int _top;

    public CompletionList(CodeView view)
    {
        _view = view;
        ClipToBounds = true;
    }

    public event Action? Committed;

    public event Action? SelectionChanged;

    public int Count => _rows.Count;

    public LspCompletion? Selected => _selected >= 0 && _selected < _rows.Count ? _rows[_selected].Item : null;

    /// <summary>The chosen one's declaration, under the rows.</summary>
    public string? Detail
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }
    }

    public void SetItems(List<CompletionFilter.Ranked> rows, int selected)
    {
        var before = Selected;
        _rows = rows;
        _top = 0;
        Select(selected);
        if (Selected != before)
        {
            Detail = null;
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Move(int by) => Select(Math.Clamp(_selected + by, 0, _rows.Count - 1));

    private void Select(int index)
    {
        var changed = index != _selected;
        _selected = index;
        if (_selected < _top)
        {
            _top = _selected;
        }
        else if (_selected >= _top + Visible)
        {
            _top = _selected - Visible + 1;
        }

        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - Visible));
        InvalidateVisual();
        if (changed || Detail is null)
        {
            Detail = null;
            SelectionChanged?.Invoke();
        }
    }

    private double DetailHeight => Detail is null ? 0 : RowHeight + 5;

    protected override Size MeasureOverride(Size availableSize) => new(Width0, Math.Min(Visible, Math.Max(1, _rows.Count)) * RowHeight + DetailHeight);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var index = _top + (int)(e.GetPosition(this).Y / RowHeight);
        if (index >= 0 && index < _rows.Count && e.GetPosition(this).Y < Math.Min(Visible, _rows.Count) * RowHeight)
        {
            Select(index);
            if (e.ClickCount >= 2)
            {
                Committed?.Invoke();
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _top = Math.Clamp(_top - (int)Math.Round(e.Delta.Y * 3), 0, Math.Max(0, _rows.Count - Visible));
        InvalidateVisual();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var foreground = _view.Foreground;
        var muted = _view.Theme("TextMutedBrush") ?? foreground;
        var selected = _view.Theme("BgSelectedBrush");

        // The names in the window's own font, as Visual Studio's list (the typed letters a little heavier); the declaration in the code's.
        var ui = TextElement.GetFontFamily(this);
        var typeface = new Typeface(ui);
        var bold = new Typeface(ui, FontStyle.Normal, FontWeight.SemiBold);
        var code = new Typeface(_view.FontFamily);
        var size = 13.0;
        var shown = Math.Min(Visible, _rows.Count);
        for (var i = 0; i < shown; i++)
        {
            var index = _top + i;
            var (item, _, matched) = _rows[index];
            var y = i * RowHeight;
            if (index == _selected && selected is not null)
            {
                context.DrawRectangle(selected, null, new Rect(0, y, Bounds.Width, RowHeight), 4, 4);
            }

            if (CodeCompletion.KindOf(item.Kind) is { } kind)
            {
                CodeSymbolIcon.Draw(context, kind, new Point(5, y + (RowHeight - 16) / 2), muted);
            }

            // The name, the letters typed in bold.
            var runs = new List<ValueSpan<TextRunProperties>>();
            var set = matched.ToHashSet();
            for (var c = 0; c < item.Label.Length;)
            {
                var isMatch = set.Contains(c);
                var end = c;
                while (end < item.Label.Length && set.Contains(end) == isMatch)
                {
                    end++;
                }

                runs.Add(new ValueSpan<TextRunProperties>(c, end - c, new GenericTextRunProperties(isMatch ? bold : typeface, size, foregroundBrush: isMatch ? foreground : foreground)));
                c = end;
            }

            using var layout = new TextLayout(item.Label, typeface, size, foreground, maxWidth: Bounds.Width - TextInset - 6, maxHeight: RowHeight,
                textTrimming: TextTrimming.CharacterEllipsis, textStyleOverrides: runs);
            layout.Draw(context, new Point(TextInset, y + (RowHeight - layout.Height) / 2));
        }

        // A thin bar showing where in the list the rows are.
        if (_rows.Count > Visible && _view.Theme("TextMutedBrush") is ISolidColorBrush thumb)
        {
            var track = shown * RowHeight;
            var height = Math.Max(16, track * Visible / _rows.Count);
            var top = (track - height) * _top / (_rows.Count - Visible);
            context.DrawRectangle(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(thumb.Color, 0.35), null, new Rect(Bounds.Width - 3, top, 2, height), 1, 1);
        }

        if (Detail is { } detail)
        {
            var y = shown * RowHeight + 4;
            if (_view.Theme("BorderDividerBrush") is { } divider)
            {
                context.FillRectangle(divider, new Rect(2, y - 2, Bounds.Width - 4, 1));
            }

            using var layout = new TextLayout(detail, code, _view.FontSize - 1, muted, maxWidth: Bounds.Width - 12, maxHeight: RowHeight, textTrimming: TextTrimming.CharacterEllipsis);
            layout.Draw(context, new Point(6, y + (RowHeight - layout.Height) / 2));
        }
    }
}
