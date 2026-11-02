using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Nomad.Core.Code;

namespace Nomad.App.Ide;

// Quick Info's names as links, as Visual Studio's: the name itself goes to its declaration, its type (the one it is in)
// and the types in its signature to theirs. Under the pointer one is underlined with a hand cursor. The card stays
// while the pointer moves from the name onto it, so they can be reached.
public sealed partial class CodeView
{
    // How long the card waits for the pointer that left the name (or the card) to come onto it (or back).
    private static readonly TimeSpan QuickLeaveDelay = TimeSpan.FromMilliseconds(300);

    private static readonly HashSet<string> SignatureKeywords =
    [
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort", "object", "string",
        "void", "dynamic", "nint", "nuint", "var", "class", "struct", "record", "interface", "enum", "delegate", "namespace", "event",
        "readonly", "static", "const", "ref", "out", "in", "params", "this", "new", "get", "set", "init", "where", "unmanaged", "notnull",
        "async", "await", "null", "true", "false", "default", "local", "variable", "parameter", "field", "property", "method", "constant",
    ];

    private DispatcherTimer? _quickLeaveTimer;
    private bool _quickCardHovered;

    /// <summary>The pointer left the name or the card: the card goes a moment later unless the pointer is on it by then.</summary>
    private void LeaveQuickInfo()
    {
        if (_quickLeaveTimer is null)
        {
            _quickLeaveTimer = new DispatcherTimer { Interval = QuickLeaveDelay };
            _quickLeaveTimer.Tick += (_, _) =>
            {
                _quickLeaveTimer.Stop();
                if (!_quickCardHovered)
                {
                    HideQuickInfo();
                    _quickAt = null;
                    UpdateQuickInfo();
                }
            };
        }

        // Already waiting: moving on doesn't put it off.
        if (!_quickLeaveTimer.IsEnabled)
        {
            _quickLeaveTimer.Start();
        }
    }

    private void OnQuickCardEntered(object? sender, PointerEventArgs e)
    {
        _quickCardHovered = true;
        _quickLeaveTimer?.Stop();
    }

    private void OnQuickCardExited(object? sender, PointerEventArgs e)
    {
        _quickCardHovered = false;
        if (_quickShown)
        {
            LeaveQuickInfo();
        }
    }

    /// <summary>Makes the signature's names links: the name itself to <paramref name="self"/>, the types by name.</summary>
    private void LinkSignature(TextBlock signature, QuickInfoView info, CodeSpot? self)
    {
        var links = SignatureLinks(info, self);
        if (links.Count == 0)
        {
            return;
        }

        SplitRuns(signature, [.. links.SelectMany(l => new[] { l.From, l.To })]);
        (int From, int To, Action Go)? hovered = null;

        void Track(PointerEventArgs e)
        {
            // An underline just changed re-lays the text out: until it has, it can't be hit-tested.
            if (!signature.IsMeasureValid)
            {
                return;
            }

            var link = LinkAt(signature, links, e.GetPosition(signature));
            if (link != hovered)
            {
                hovered = link;
                Underline(signature, link);
                signature.Cursor = link is null ? GutterCursor : LinkCursor;
            }
        }

        signature.PointerEntered += (_, e) => Track(e);
        signature.PointerMoved += (_, e) => Track(e);
        signature.PointerExited += (_, _) =>
        {
            hovered = null;
            Underline(signature, null);
            signature.Cursor = GutterCursor;
        };

        // The link the pointer is on (the underline re-lays the text out, so a hit test right after it misses).
        signature.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(signature).Properties.IsLeftButtonPressed && hovered is { } link)
            {
                _quickCardHovered = false;
                HideQuickInfo();
                link.Go();
                e.Handled = true;
            }
        };
    }

    // The names that lead somewhere: the one asked about (its first whole-word place outside the parameters), the type
    // it is a member of (just before it), and every other type-like name (capitalized, not a keyword, not a namespace).
    private List<(int From, int To, Action Go)> SignatureLinks(QuickInfoView info, CodeSpot? self)
    {
        var links = new List<(int From, int To, Action Go)>();
        if (Navigator is not { } navigator)
        {
            return links;
        }

        var text = info.Signature;
        var selfAt = self is { Name.Length: > 0 } spot ? NameOutsideParentheses(text, spot.Name) : -1;
        var isType = info.Kind is SymbolKind.Class or SymbolKind.Record or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum or SymbolKind.Delegate;
        foreach (Match word in Regex.Matches(text, @"[A-Za-z_]\w*"))
        {
            var (from, to, name) = (word.Index, word.Index + word.Length, word.Value);
            var dotted = to < text.Length && text[to] == '.';
            if (from == selfAt)
            {
                links.Add((from, to, () => navigator.GoToDefinition(self!.Value)));
            }
            else if (char.IsUpper(name[0]) && !SignatureKeywords.Contains(name) && (!dotted || !isType && to + 1 == selfAt))
            {
                links.Add((from, to, () => navigator.GoToType(name)));
            }
        }

        return links;
    }

    // Where a name stands as a whole word outside parentheses (the parameters' names aside), or -1.
    private static int NameOutsideParentheses(string text, string name)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] == '(' ? 1 : text[i] == ')' ? -1 : 0;
            if (depth == 0 && string.CompareOrdinal(text, i, name, 0, name.Length) == 0 &&
                (i == 0 || !IsWordChar(text[i - 1])) && (i + name.Length == text.Length || !IsWordChar(text[i + name.Length])))
            {
                return i;
            }
        }

        return -1;

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    private static (int From, int To, Action Go)? LinkAt(TextBlock block, List<(int From, int To, Action Go)> links, Point point)
    {
        var hit = block.TextLayout.HitTestPoint(point - new Point(block.Padding.Left, block.Padding.Top));
        if (!hit.IsInside)
        {
            return null;
        }

        var index = hit.TextPosition;
        return links.FirstOrDefault(l => index >= l.From && index < l.To) is { Go: not null } link ? link : null;
    }

    // Cuts the runs at these places (keeping their looks), so a link's runs are its own.
    private static void SplitRuns(TextBlock block, IReadOnlyList<int> cuts)
    {
        var runs = block.Inlines!.ToList();
        block.Inlines.Clear();
        var at = 0;
        foreach (var inline in runs)
        {
            if (inline is not Run { Text: { } text } run)
            {
                block.Inlines.Add(inline);
                continue;
            }

            var edges = cuts.Where(c => c > at && c < at + text.Length).Distinct().Order().Append(at + text.Length);
            var from = at;
            foreach (var edge in edges)
            {
                block.Inlines.Add(new Run(text[(from - at)..(edge - at)])
                {
                    Foreground = run.Foreground,
                    Background = run.Background,
                    FontWeight = run.FontWeight,
                });
                from = edge;
            }

            at += text.Length;
        }
    }

    // Underlines the runs inside the link (the runs cover the signature in order, so their places add up), none for null.
    private static void Underline(TextBlock block, (int From, int To, Action Go)? link)
    {
        var at = 0;
        foreach (var inline in block.Inlines!)
        {
            if (inline is Run run)
            {
                var length = run.Text?.Length ?? 0;
                run.TextDecorations = link is { } l && at >= l.From && at + length <= l.To ? TextDecorations.Underline : null;
                at += length;
            }
        }
    }
}
