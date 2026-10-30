using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Ide;

namespace Nomad.App.ViewModels;

/// <summary>
/// An editor group's find and replace (Ctrl+F, Ctrl+H), as Visual Studio's find box over the code: matching case,
/// whole words or a regular expression, line by line. Every match is picked out in the text and beside the scroll bar
/// (the document's marks); Enter and F3 go to the next, Shift for the previous. Replace All is one undo step.
/// </summary>
public sealed partial class FindViewModel : ObservableObject
{
    private const int MaxMatches = 20_000;

    private readonly EditorGroup _group;
    private readonly DispatcherTimer _debounce;
    private CodeDocument? _doc;
    private List<(TextPos From, TextPos To)> _matches = [];
    private int _current = -1;
    private Regex? _regex;

    public FindViewModel(EditorGroup group)
    {
        _group = group;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Recompute(keepCurrent: true);
        };
    }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>In the visual tree: from opening until it has faded out.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; }

    /// <summary>Faded in: set a frame after <see cref="IsShown"/>.</summary>
    [ObservableProperty]
    public partial bool IsRaised { get; set; }

    [ObservableProperty]
    public partial bool IsReplaceShown { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string ReplaceText { get; set; } = "";

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool WholeWord { get; set; }

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    /// <summary>"3 of 12", "No results", or what is wrong with the expression.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "";

    /// <summary>Nothing matches (or the expression is wrong): the box turns red.</summary>
    [ObservableProperty]
    public partial bool HasNoMatch { get; set; }

    /// <summary>Raised to put the focus in the find box (opening it, or Ctrl+F while it is open).</summary>
    public event Action<bool>? FocusRequested;

    partial void OnQueryChanged(string value) => Recompute(keepCurrent: false);

    partial void OnMatchCaseChanged(bool value) => Recompute(keepCurrent: false);

    partial void OnWholeWordChanged(bool value) => Recompute(keepCurrent: false);

    partial void OnUseRegexChanged(bool value) => Recompute(keepCurrent: false);

    [RelayCommand]
    private void ToggleMatchCase() => MatchCase = !MatchCase;

    [RelayCommand]
    private void ToggleWholeWord() => WholeWord = !WholeWord;

    [RelayCommand]
    private void ToggleRegex() => UseRegex = !UseRegex;

    [RelayCommand]
    private void ToggleReplace() => IsReplaceShown = !IsReplaceShown;

    /// <summary>Opens it (with replace for Ctrl+H), taking the selected text as the query when it is on one line.</summary>
    public void Open(bool replace)
    {
        if (_group.ActiveTab?.Document is { } doc && doc.Anchor != doc.Caret && doc.Anchor.Line == doc.Caret.Line)
        {
            Query = doc.GetText(doc.Anchor, doc.Caret);
        }

        // Replace shows once the box is on screen, so its chevron's turn runs (a transition on a hidden control stalls).
        if (!IsOpen)
        {
            IsOpen = true;
            IsShown = true;
            Dispatcher.UIThread.Post(() =>
            {
                IsRaised = IsOpen;
                IsReplaceShown |= replace;
            }, DispatcherPriority.Loaded);
        }
        else
        {
            IsReplaceShown |= replace;
        }

        Attach(_group.ActiveTab?.Document);
        Recompute(keepCurrent: false);
        FocusRequested?.Invoke(replace);
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        IsRaised = false;
        Attach(null);
        DispatcherTimer.RunOnce(() => IsShown = IsOpen, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>The group shows another file: the find goes on in it.</summary>
    public void OnActiveTabChanged()
    {
        if (IsOpen)
        {
            Attach(_group.ActiveTab?.Document);
            Recompute(keepCurrent: false);
        }
    }

    [RelayCommand]
    public void Next() => Go(1);

    [RelayCommand]
    public void Previous() => Go(-1);

    /// <summary>Replaces the match at hand (when it is the one selected), then goes on to the next.</summary>
    [RelayCommand]
    private void Replace()
    {
        if (_doc is not { IsReadOnly: false } doc || _current < 0 || _current >= _matches.Count)
        {
            Go(1);
            return;
        }

        var (from, to) = _matches[_current];
        var selected = (doc.Anchor <= doc.Caret ? (doc.Anchor, doc.Caret) : (doc.Caret, doc.Anchor)) == (from, to);
        if (!selected)
        {
            Select(_current);
            return;
        }

        doc.Seal();
        doc.Replace(from, to, Substitute(doc, from, to));
        doc.Seal();
        Recompute(keepCurrent: false);
        if (_matches.Count > 0)
        {
            Select(_current);
        }
    }

    /// <summary>Replaces every match, as one undo step.</summary>
    [RelayCommand]
    private void ReplaceAll()
    {
        if (_doc is not { IsReadOnly: false } doc || _matches.Count == 0)
        {
            return;
        }

        var count = _matches.Count;
        var edits = _matches.Select(m => (m.From, m.To, Text: Substitute(doc, m.From, m.To))).ToList();
        doc.BeginGroup();
        for (var i = edits.Count - 1; i >= 0; i--)
        {
            doc.Replace(edits[i].From, edits[i].To, edits[i].Text);
        }

        doc.EndGroup();
        Recompute(keepCurrent: false);
        Status = count == 1 ? "1 replaced" : $"{count} replaced";
    }

    // The replacement for a match: as typed, or with the expression's $1 and the like filled in.
    private string Substitute(CodeDocument doc, TextPos from, TextPos to)
    {
        if (!UseRegex || _regex is null)
        {
            return ReplaceText;
        }

        var line = doc.Lines[from.Line];
        var match = _regex.Match(line, from.Index);
        return match.Success && match.Index == from.Index ? match.Result(ReplaceText) : ReplaceText;
    }

    private void Go(int by)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        // From the caret: the first match after it (or before it, going back), wrapping round.
        if (_doc is { } doc)
        {
            var at = by > 0 ? (doc.Anchor >= doc.Caret ? doc.Anchor : doc.Caret) : (doc.Anchor <= doc.Caret ? doc.Anchor : doc.Caret);
            var index = by > 0
                ? _matches.FindIndex(m => m.From >= at && !(m.From == at && m.To == at))
                : _matches.FindLastIndex(m => m.To <= at && m.From < at);
            _current = index >= 0 ? index : by > 0 ? 0 : _matches.Count - 1;
        }

        Select(_current);
    }

    private void Select(int index)
    {
        if (_doc is not { } doc || index < 0 || index >= _matches.Count)
        {
            return;
        }

        _current = index;
        var (from, to) = _matches[index];
        doc.SetMarks(_matches, index);
        doc.Select(from, to);
        Status = $"{index + 1} of {_matches.Count}";
    }

    private void Attach(CodeDocument? doc)
    {
        if (_doc == doc)
        {
            return;
        }

        if (_doc is not null)
        {
            _doc.Changed -= OnDocumentChanged;
            _doc.SetMarks([], -1);
        }

        _doc = doc;
        if (_doc is not null)
        {
            _doc.Changed += OnDocumentChanged;
        }
    }

    // Edits move the matches: worked out again once typing pauses.
    private void OnDocumentChanged()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Recompute(bool keepCurrent)
    {
        if (!IsOpen || _doc is not { } doc)
        {
            return;
        }

        _regex = null;
        _matches = [];
        if (Query.Length > 0)
        {
            try
            {
                var pattern = UseRegex ? Query : Regex.Escape(Query);
                if (WholeWord)
                {
                    pattern = $@"(?<![\w]){pattern}(?![\w])";
                }

                var options = RegexOptions.CultureInvariant | (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                _regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(250));
                var lines = doc.Lines;
                for (var i = 0; i < lines.Count && _matches.Count < MaxMatches; i++)
                {
                    for (var m = _regex.Match(lines[i]); m.Success && _matches.Count < MaxMatches; m = m.NextMatch())
                    {
                        if (m.Length > 0)
                        {
                            _matches.Add((new TextPos(i, m.Index), new TextPos(i, m.Index + m.Length)));
                        }
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
            {
                _matches = [];
                HasNoMatch = true;
                Status = e is RegexMatchTimeoutException ? "Too slow" : "Not a valid expression";
                doc.SetMarks([], -1);
                return;
            }
        }

        // The match at hand: the same one after an edit, else the first at or after the caret.
        var caret = doc.Anchor <= doc.Caret ? doc.Anchor : doc.Caret;
        var current = keepCurrent && _current >= 0 && _current < _matches.Count ? _current : _matches.FindIndex(m => m.From >= caret);
        _current = _matches.Count == 0 ? -1 : current < 0 ? 0 : current;
        HasNoMatch = Query.Length > 0 && _matches.Count == 0;
        Status = Query.Length == 0 ? "" : _matches.Count == 0 ? "No results" : $"{_current + 1} of {_matches.Count}{(_matches.Count >= MaxMatches ? "+" : "")}";
        doc.SetMarks(_matches, _current);

        // Typing the query takes the caret to the first match, as Visual Studio's incremental find does.
        if (!keepCurrent && _current >= 0)
        {
            var (from, to) = _matches[_current];
            doc.Select(from, to);
        }
    }
}
