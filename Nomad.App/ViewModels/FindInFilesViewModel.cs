using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Core.Code;

namespace Nomad.App.ViewModels;

/// <summary>
/// The Files view's find in files (Ctrl+Shift+F there), as Visual Studio's: a text (or a regular expression) in every
/// file of the folder, the matches listed under their files. It searches as the query changes; Enter or a click opens
/// a match with it selected. The query and results stay for the next time it opens.
/// </summary>
public sealed partial class FindInFilesViewModel(FilesViewModel files) : ObservableObject
{
    private const int MaxHits = 2000;
    private CancellationTokenSource? _cts;

    // Enter came before the results did: the first one opens when they come.
    private bool _openWhenReady;

    public ObservableCollection<FindInFilesRow> Results { get; } = [];

    /// <summary>Open: it takes the keys (Esc, arrows, Enter).</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>In the visual tree: from opening until it has faded out.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>Faded in: set a frame after <see cref="IsShown"/>, so the fade runs from nothing.</summary>
    [ObservableProperty]
    public partial bool IsRaised { get; private set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool WholeWord { get; set; }

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    /// <summary>Beside the box: "Searching…", "No results", "12 in 3 files".</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasResults { get; private set; }

    [ObservableProperty]
    public partial int SelectedIndex { get; private set; } = -1;

    // A list of places shown under a title (implementations): the title in the box searches nothing.
    private bool _listing;

    partial void OnQueryChanged(string value)
    {
        if (!_listing)
        {
            Run();
        }
    }

    partial void OnMatchCaseChanged(bool value) => Run();

    partial void OnWholeWordChanged(bool value) => Run();

    partial void OnUseRegexChanged(bool value) => Run();

    partial void OnSelectedIndexChanged(int oldValue, int newValue)
    {
        if (oldValue >= 0 && oldValue < Results.Count)
        {
            Results[oldValue].IsSelected = false;
        }

        if (newValue >= 0 && newValue < Results.Count)
        {
            Results[newValue].IsSelected = true;
        }
    }

    [RelayCommand]
    private void ToggleMatchCase() => MatchCase = !MatchCase;

    [RelayCommand]
    private void ToggleWholeWord() => WholeWord = !WholeWord;

    [RelayCommand]
    private void ToggleRegex() => UseRegex = !UseRegex;

    /// <summary>Opens it, with a query to start from (the text selected in the file, if any).</summary>
    public void Open(string? query = null)
    {
        if (files.Folder is not { } folder || !Directory.Exists(folder))
        {
            return;
        }

        if (!string.IsNullOrEmpty(query) && query != Query)
        {
            Query = query;
        }
        else if (Query.Length > 0)
        {
            Run(); // The files may have changed since.
        }

        IsOpen = true;
        IsShown = true;
        Dispatcher.UIThread.Post(() => IsRaised = IsOpen, DispatcherPriority.Loaded);
    }

    /// <summary>Opens it on a list of places (what a language server found), under a title, as its results are shown.</summary>
    public void ShowLocations(string title, IReadOnlyList<Nomad.Core.Lsp.LspLocation> locations)
    {
        if (files.Folder is not { } folder)
        {
            return;
        }

        _cts?.Cancel();
        var hits = new List<TextHit>();
        foreach (var location in locations.OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Line))
        {
            string text;
            try
            {
                text = File.ReadLines(location.Path).Skip(location.Line).FirstOrDefault() ?? "";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                text = "";
            }

            var column = Math.Min(location.Column, text.Length);
            var length = location.EndLine == location.Line ? Math.Clamp(location.EndColumn - column, 0, text.Length - column) : 0;
            hits.Add(new TextHit(location.Path, location.Line, column, length, text));
        }

        _listing = true;
        Query = title;
        _listing = false;
        var rows = new List<FindInFilesRow>();
        foreach (var file in hits.GroupBy(h => h.Path))
        {
            rows.Add(FindInFilesRow.ForFile(file.Key, folder, file.Count()));
            rows.AddRange(file.Select(FindInFilesRow.ForHit));
        }

        Show(rows, $"{hits.Count} {(hits.Count == 1 ? "place" : "places")}");
        IsOpen = true;
        IsShown = true;
        Dispatcher.UIThread.Post(() => IsRaised = IsOpen, DispatcherPriority.Loaded);
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        IsRaised = false;
        DispatcherTimer.RunOnce(() => IsShown = IsOpen, TimeSpan.FromMilliseconds(220));
    }

    /// <summary>Up or Down through the matches (the files' rows are passed over), or a page at a time.</summary>
    public void Move(int by)
    {
        var matches = Enumerable.Range(0, Results.Count).Where(i => !Results[i].IsFile).ToList();
        if (matches.Count == 0)
        {
            return;
        }

        var at = matches.IndexOf(SelectedIndex);
        var next = at < 0 ? (by > 0 ? 0 : matches.Count - 1)
            : by is 1 or -1 ? (at + by + matches.Count) % matches.Count
            : Math.Clamp(at + by, 0, matches.Count - 1);
        SelectedIndex = matches[next];
    }

    /// <summary>Opens a match (the selected one if none is given) with it selected, and closes.</summary>
    public void OpenResult(FindInFilesRow? row = null)
    {
        if ((row ?? (SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null)) is not { Hit: { } hit } chosen)
        {
            _openWhenReady = row is null && Status == "Searching…";
            return;
        }

        SelectedIndex = Results.IndexOf(chosen);
        Close();
        files.OpenAt(hit.Path, (hit.Line, hit.Column), length: hit.Length);
    }

    // Searches a moment after the typing stops; a newer query cancels the one running.
    private void Run()
    {
        _openWhenReady = false;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (Query.Length == 0 || files.Folder is not { } folder)
        {
            Show([], "");
            return;
        }

        Regex pattern;
        try
        {
            pattern = TextSearch.Pattern(Query, MatchCase, WholeWord, UseRegex);
        }
        catch (ArgumentException)
        {
            Show([], "Not a valid expression");
            return;
        }

        Status = "Searching…";
        _ = SearchAsync(folder, pattern, cts.Token);
    }

    private async Task SearchAsync(string folder, Regex pattern, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(180, cancel);
            var hits = await Task.Run(() => TextSearch.SearchAsync(folder, pattern, MaxHits, cancel), cancel);
            if (cancel.IsCancellationRequested)
            {
                return;
            }

            var rows = new List<FindInFilesRow>();
            foreach (var file in hits.GroupBy(h => h.Path))
            {
                rows.Add(FindInFilesRow.ForFile(file.Key, folder, file.Count()));
                rows.AddRange(file.Select(FindInFilesRow.ForHit));
            }

            var fileCount = rows.Count - hits.Count;
            Show(rows, hits.Count == 0 ? "No results"
                : $"{hits.Count}{(hits.Count >= MaxHits ? "+" : "")} in {fileCount} {(fileCount == 1 ? "file" : "files")}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Show(List<FindInFilesRow> rows, string status)
    {
        SelectedIndex = -1;
        Results.Clear();
        foreach (var row in rows)
        {
            Results.Add(row);
        }

        HasResults = rows.Count > 0;
        Status = status;
        if (rows.Count > 0)
        {
            Move(1);
        }

        if (_openWhenReady)
        {
            _openWhenReady = false;
            OpenResult();
        }
    }
}

/// <summary>A row of find in files: a file (its name, folder and number of matches), or one match in it.</summary>
public sealed partial class FindInFilesRow : ObservableObject
{
    private const int Lead = 48;

    private FindInFilesRow()
    {
    }

    public bool IsFile { get; private init; }

    public bool IsMatch => !IsFile;

    public string Name { get; private init; } = "";

    public string Folder { get; private init; } = "";

    public string Count { get; private init; } = "";

    public TextHit? Hit { get; private init; }

    public string LineNumber { get; private init; } = "";

    public string Before { get; private init; } = "";

    public string Match { get; private init; } = "";

    public string After { get; private init; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public static FindInFilesRow ForFile(string path, string root, int count)
    {
        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(path) ?? root).Replace('\\', '/');
        return new FindInFilesRow
        {
            IsFile = true,
            Name = Path.GetFileName(path),
            Folder = relative == "." ? "" : relative,
            Count = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    // The line without its indent, with no more than a little of it before the match.
    public static FindInFilesRow ForHit(TextHit hit)
    {
        var text = hit.Text.Replace('\t', ' ');
        var start = text.Length - text.TrimStart().Length;
        var before = text[Math.Min(start, hit.Column)..hit.Column];
        if (before.Length > Lead)
        {
            before = "…" + before[^Lead..];
        }

        var end = Math.Min(text.Length, hit.Column + hit.Length);
        var after = text[end..];
        return new FindInFilesRow
        {
            Hit = hit,
            LineNumber = (hit.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Before = before,
            Match = text[hit.Column..end],
            After = after.Length > 200 ? after[..200] : after,
        };
    }
}
