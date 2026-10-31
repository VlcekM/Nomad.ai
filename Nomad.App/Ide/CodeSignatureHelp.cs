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
/// The code's signature help, as Visual Studio's Parameter Info: typing a '(' or ',' in a call shows the method's
/// declaration above it, the argument being typed in bold with what it is for, and "1 of 3" with Up and Down through
/// the overloads. It follows the caret through the call and goes when the caret leaves it, or on Esc
/// (Ctrl+Shift+Space brings it back).
/// </summary>
internal sealed class CodeSignatureHelp
{
    private const double FadeMs = 130;

    private readonly CodeView _view;
    private readonly Popup _popup;
    private readonly Border _card;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(FadeMs) };
    private CancellationTokenSource? _cts;
    private int _serial;

    private LspSignatureHelp? _help;
    private int _shown;

    // The overload chosen with Up and Down: kept while the caret goes through the call.
    private string? _chosen;

    // Where the call's '(' was when it opened (the popup hangs above it).
    private TextPos _anchor;

    public CodeSignatureHelp(CodeView view, Action<Popup> adopt)
    {
        _view = view;
        _card = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7),
            MaxWidth = 720,
            Opacity = 0,
            IsHitTestVisible = false,
            Transitions =
            [
                new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(FadeMs), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
            ],
        };
        _popup = new Popup
        {
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = _card,
            PlacementTarget = view,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.TopRight,
            IsLightDismissEnabled = false,
            Focusable = false,
            IsHitTestVisible = false,
        };
        adopt(_popup);
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Stop();
            Ask(null, retrigger: true);
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (!IsOpen)
            {
                _popup.IsOpen = false;
                _card.Child = null;
            }
        };
    }

    /// <summary>Showing (or asked for): Esc closes it, and Up and Down go through the overloads.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>A character was typed: '(' or ',' asks for the call's signatures; anything else updates one showing.</summary>
    public void AfterTyping(string text)
    {
        if (text is "(" or "," or "<")
        {
            if (text == "<" && !IsOpen)
            {
                return; // A comparison far more often than a generic's arguments: only followed once open.
            }

            Ask(text, retrigger: IsOpen);
        }
        else if (IsOpen)
        {
            Schedule();
        }
    }

    /// <summary>Ctrl+Shift+Space: the signatures of the call at the caret.</summary>
    public void Start() => Ask(null, retrigger: false);

    /// <summary>The caret moved or the text changed: the argument it is at is asked again a moment later.</summary>
    public void Update()
    {
        if (IsOpen)
        {
            Schedule();
        }
    }

    private void Schedule()
    {
        _updateTimer.Stop();
        _updateTimer.Start();
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsOpen || e.KeyModifiers != KeyModifiers.None)
        {
            return false;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            return true;
        }

        if (e.Key is Key.Up or Key.Down && _help is { Signatures.Count: > 1 } help)
        {
            _shown = (_shown + (e.Key == Key.Down ? 1 : help.Signatures.Count - 1)) % help.Signatures.Count;
            _chosen = help.Signatures[_shown].Label;
            Render();
            return true;
        }

        return false;
    }

    private void Ask(string? trigger, bool retrigger)
    {
        var doc = _view.Doc;
        if (_view.Navigator is not { } navigator || doc.IsReadOnly || _view.InCommentOrString(doc.Caret))
        {
            Close();
            return;
        }

        _updateTimer.Stop();
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var serial = ++_serial;
        var caret = doc.Caret;
        _ = AskAsync(navigator, caret, trigger, retrigger, serial, cts.Token);
    }

    private async Task AskAsync(ICodeNavigator navigator, TextPos caret, string? trigger, bool retrigger, int serial, CancellationToken cancellationToken)
    {
        LspSignatureHelp? help;
        try
        {
            help = await navigator.SignatureHelpAsync(caret.Line, caret.Index, trigger, retrigger, cancellationToken);

            // Typed on meanwhile, the server may have dropped the question: once more where the caret is now.
            if (help is null && trigger is not null && serial == _serial && !cancellationToken.IsCancellationRequested && _view.Doc.Caret != caret)
            {
                caret = _view.Doc.Caret;
                help = await navigator.SignatureHelpAsync(caret.Line, caret.Index, null, retrigger, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // A newer question is on its way (the caret moved, the text changed): its answer counts.
        if (serial != _serial || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (help is null)
        {
            Close();
            return;
        }

        var opening = !IsOpen;
        _help = help;
        var chosen = _chosen is { } label ? help.Signatures.ToList().FindIndex(s => s.Label == label) : -1;
        _shown = chosen >= 0 ? chosen : help.ActiveSignature;
        if (opening)
        {
            // Above the call: from the '(' before the caret, or the caret.
            var line = _view.Doc.Lines[caret.Line];
            var paren = line.LastIndexOf('(', Math.Clamp(caret.Index - 1, 0, Math.Max(0, line.Length - 1)));
            _anchor = new TextPos(caret.Line, paren >= 0 ? paren + 1 : caret.Index);
        }

        IsOpen = true;
        Render();
        Show(opening);
    }

    private void Show(bool opening)
    {
        _closeTimer.Stop();
        _card.Background = _view.Theme("BgMenuBrush");
        _card.BorderBrush = _view.Theme("BorderCardBrush");
        (_popup.PlacementTarget, _popup.PlacementRect) = _view.WindowAnchor(_view.NameRect(_anchor.Line, _anchor.Index));
        _popup.HorizontalOffset = -12;
        _popup.VerticalOffset = -2;
        if (!_popup.IsOpen || opening)
        {
            _card.Opacity = 0;
            _popup.IsOpen = true;
            Dispatcher.UIThread.Post(() => _card.Opacity = IsOpen ? 1 : 0, DispatcherPriority.Loaded);
        }
        else
        {
            _card.Opacity = 1;
        }
    }

    /// <summary>The text scrolled: it follows its call.</summary>
    public void Reposition()
    {
        if (IsOpen && _popup.IsOpen)
        {
            (_popup.PlacementTarget, _popup.PlacementRect) = _view.WindowAnchor(_view.NameRect(_anchor.Line, _anchor.Index));
        }
    }

    public void Close()
    {
        _updateTimer.Stop();
        _cts?.Cancel();
        _serial++;
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _help = null;
        _chosen = null;
        if (_popup.IsOpen)
        {
            _card.Opacity = 0;
            _closeTimer.Start();
        }
    }

    // "1 of 3", the declaration with the argument at hand in bold, then what the method and that argument are for.
    private void Render()
    {
        if (_help is not { } help || _shown >= help.Signatures.Count)
        {
            return;
        }

        var signature = help.Signatures[_shown];
        var active = signature.ActiveParameter ?? help.ActiveParameter;
        var muted = _view.Theme("TextMutedBrush") ?? _view.Foreground;
        var declaration = new TextBlock { FontFamily = _view.FontFamily, FontSize = _view.FontSize, TextWrapping = TextWrapping.Wrap, Foreground = _view.Foreground };
        if (help.Signatures.Count > 1)
        {
            declaration.Inlines!.Add(new Run($"{_shown + 1} of {help.Signatures.Count}  ") { Foreground = muted });
        }

        var label = signature.Label;
        var grammar = Highlighter.ForLanguage("csharp");
        if (active >= 0 && active < signature.Parameters.Count && signature.Parameters[active] is var (start, end, _) && end > start && end <= label.Length)
        {
            _view.AddCode(declaration.Inlines!, label, grammar, start, end);
        }
        else
        {
            _view.AddCode(declaration.Inlines!, label, grammar);
        }

        var panel = new StackPanel { Spacing = 4, Children = { declaration } };
        if (Plain(signature.Documentation) is { } summary)
        {
            panel.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Foreground = muted });
        }

        if (active >= 0 && active < signature.Parameters.Count && signature.Parameters[active] is var (from, to, doc) && Plain(doc) is { } about)
        {
            var name = to > from && to <= label.Length ? label[from..to].Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1] : null;
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = muted };
            if (name is not null)
            {
                line.Inlines!.Add(new Run(name + ": ") { FontWeight = FontWeight.SemiBold, Foreground = _view.Foreground });
            }

            line.Inlines!.Add(new Run(about));
            panel.Children.Add(line);
        }

        _card.Child = panel;
    }

    // Documentation as plain text (a code block's fences, markdown's escapes and emphasis gone); null for none.
    private static string? Plain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var text = markdown.Replace("\r\n", "\n").Replace("```csharp", "").Replace("```", "").Replace("**", "").Replace("`", "").Replace("\\", "");
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        var joined = string.Join("\n", lines);
        return joined.Length > 0 ? joined : null;
    }
}
