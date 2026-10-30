using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.Core.Code;
using Nomad.Core.Files;

namespace Nomad.App.ViewModels;

/// <summary>
/// The Files view's code search (Ctrl+T there), as Visual Studio's Go To All: types, members and files of the folder
/// by name, matched by prefix, word starts or anywhere. The folder is indexed the first time it opens, and brought up
/// to date (only the files that changed) each time after; results come from what is indexed so far.
/// </summary>
public sealed partial class CodeSearchViewModel(FilesViewModel files) : ObservableObject
{
    private readonly ConcurrentDictionary<string, string> _projects = new(StringComparer.OrdinalIgnoreCase);
    private CodeIndex? _index;
    private CancellationTokenSource? _queryCts;
    private Task? _refreshing;

    public ObservableCollection<CodeSearchResult> Results { get; } = [];

    /// <summary>Open: it takes the keys (Esc, arrows, Enter).</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>In the visual tree: from opening until it has faded out.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; }

    /// <summary>Faded in: set a frame after <see cref="IsShown"/>, so the fade runs from nothing.</summary>
    [ObservableProperty]
    public partial bool IsRaised { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>Beside the box: "Indexing…", "No results", "12 results".</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool HasResults { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected))]
    public partial int SelectedIndex { get; set; } = -1;

    public CodeSearchResult? Selected => SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    partial void OnQueryChanged(string value) => Run();

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

    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    /// <summary>A query of ":" and a number goes to that line of the file shown (Ctrl+G opens it so), as in Visual Studio.</summary>
    private static int? LineQuery(string query) =>
        query.StartsWith(':') && query.Length < 12 && query[1..].All(char.IsAsciiDigit)
            ? (query.Length > 1 ? int.Parse(query.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture) : 0)
            : null;

    /// <summary>Opens it, with a query already typed (":" for Ctrl+G) when one is given.</summary>
    public void Open(string? query = null)
    {
        if (query is not null)
        {
            Query = query;
        }

        if (files.Folder is not { } folder || !Directory.Exists(folder))
        {
            return;
        }

        if (_index is null || !string.Equals(_index.Root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase))
        {
            _index = new CodeIndex(folder);
            _projects.Clear();
        }

        IsOpen = true;
        IsShown = true;
        Dispatcher.UIThread.Post(() => IsRaised = IsOpen, DispatcherPriority.Loaded);
        Refresh(_index);
        Run();
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        IsRaised = false;
        _queryCts?.Cancel();
        DispatcherTimer.RunOnce(() => IsShown = IsOpen, TimeSpan.FromMilliseconds(220));
    }

    /// <summary>Up or Down in the list (wrapping round), or a page at a time.</summary>
    public void Move(int by)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = SelectedIndex < 0 ? (by > 0 ? 0 : Results.Count - 1) : SelectedIndex + by;
        SelectedIndex = by is 1 or -1 ? (index + Results.Count) % Results.Count : Math.Clamp(index, 0, Results.Count - 1);
    }

    /// <summary>Opens a result (the selected one if none is given) at its line, and closes the search.</summary>
    public void OpenResult(CodeSearchResult? result = null)
    {
        if (result is null && LineQuery(Query) is { } line)
        {
            if (line > 0)
            {
                Close();
                files.GoToLine(line);
            }

            return;
        }

        if ((result ?? Selected) is not { } row)
        {
            return;
        }

        Close();
        var symbol = row.Symbol;
        files.OpenAt(symbol.Path, symbol.Kind == SymbolKind.File ? null : (symbol.Line, symbol.Column));
    }

    /// <summary>
    /// F12 or Ctrl+click on a name: its declaration. One goes straight there; several open the search on the name,
    /// to pick from. A guess without a compiler (<see cref="CodeLookup"/>), from what the name is used on and where.
    /// </summary>
    public async Task GoToDefinitionAsync(CodeReference at, IReadOnlyList<string> lines)
    {
        if (Index() is not { } index)
        {
            return;
        }

        if (!index.IsReady || _refreshing is { IsCompleted: false })
        {
            Refresh(index);
            if (_refreshing is { } refreshing)
            {
                await refreshing;
            }
        }

        var found = CodeLookup.Resolve(index.Named, at, lines);
        if (found.Count == 1)
        {
            var symbol = found[0];
            files.OpenAt(symbol.Path, (symbol.Line, symbol.Column), length: symbol.Name.Length);
        }
        else
        {
            Open(at.Name);
        }
    }

    private DateTime _refreshedUtc;

    /// <summary>Starts indexing the folder (the Files view came up), so Go To and Quick Info have it when asked.</summary>
    public void Warm()
    {
        if (Index() is { IsReady: false } index)
        {
            Refresh(index);
        }
    }

    /// <summary>
    /// What a name in a file means, from what is indexed now (nothing until the first indexing is done, which this
    /// starts; it catches up with changed files every few seconds).
    /// </summary>
    public List<CodeSymbol> Resolve(CodeReference at, IReadOnlyList<string> lines)
    {
        if (Index() is not { } index)
        {
            return [];
        }

        if (!index.IsReady || DateTime.UtcNow - _refreshedUtc > TimeSpan.FromSeconds(5))
        {
            _refreshedUtc = DateTime.UtcNow;
            Refresh(index);
        }

        return index.IsReady || at.Receiver is null ? CodeLookup.Resolve(index.Named, at, lines) : [];
    }

    private readonly Dictionary<string, (DateTime Written, string[] Lines)> _fileLines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A declaration's Quick Info (the lines of the file it is in come from the editor when it is open there).</summary>
    public QuickInfo Describe(CodeSymbol symbol, string? openPath, IReadOnlyList<string>? openLines)
    {
        if (openLines is not null && string.Equals(openPath, symbol.Path, StringComparison.OrdinalIgnoreCase))
        {
            return CodeLookup.Describe(symbol, openLines);
        }

        try
        {
            var written = File.GetLastWriteTimeUtc(symbol.Path);
            if (!_fileLines.TryGetValue(symbol.Path, out var cached) || cached.Written != written)
            {
                cached = (written, File.ReadAllLines(symbol.Path));
                _fileLines[symbol.Path] = cached;
            }

            return CodeLookup.Describe(symbol, cached.Lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new QuickInfo(symbol.Name, null);
        }
    }

    // The folder's index (made the first time; null without a folder).
    private CodeIndex? Index()
    {
        if (files.Folder is not { } folder || !Directory.Exists(folder))
        {
            return null;
        }

        if (_index is null || !string.Equals(_index.Root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase))
        {
            _index = new CodeIndex(folder);
            _projects.Clear();
            _fileLines.Clear();
        }

        return _index;
    }

    // Indexing (the first time) or catching up (after): the query runs again when it is done.
    private void Refresh(CodeIndex index)
    {
        if (_refreshing is { IsCompleted: false })
        {
            return;
        }

        if (!index.IsReady)
        {
            Status = "Indexing…";
        }

        _refreshing = RefreshAsync(index);
    }

    private async Task RefreshAsync(CodeIndex index)
    {
        try
        {
            await index.RefreshAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
        }

        if (IsOpen && index == _index)
        {
            Run();
        }
    }

    private void Run()
    {
        _queryCts?.Cancel();
        if (!IsOpen || _index is not { } index)
        {
            return;
        }

        if (LineQuery(Query) is { } line)
        {
            Results.Clear();
            HasResults = false;
            SelectedIndex = -1;
            var count = files.ActiveGroup.ActiveTab?.Document.Lines.Count ?? 0;
            Status = count == 0 ? "Open a file to go to a line" : line == 0 ? $"Line number (1–{count:N0})" : $"Go to line {Math.Min(line, count):N0} of {count:N0}";
            return;
        }

        var cts = _queryCts = new CancellationTokenSource();
        var query = Query;
        var root = index.Root;
        _ = Task.Run(() =>
        {
            var matches = index.Search(query, cts.Token);
            return matches.Select(m => new CodeSearchResult(m, ProjectOf(m.Symbol.Path, root))).ToList();
        }, cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.Status != TaskStatus.RanToCompletion)
            {
                return;
            }

            Results.Clear();
            foreach (var result in t.Result)
            {
                Results.Add(result);
            }

            HasResults = Results.Count > 0;
            SelectedIndex = Results.Count > 0 ? 0 : -1;
            if (Results.Count > 0)
            {
                Results[0].IsSelected = true;
            }

            Status = !index.IsReady ? "Indexing…"
                : query.Trim().Length == 0 ? $"{index.SymbolCount:N0} symbols"
                : Results.Count == 0 ? "No results"
                : Results.Count == 1 ? "1 result" : $"{Results.Count} results";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // The project a file is in (the nearest folder above it with a project file), as Visual Studio shows on the right.
    private string ProjectOf(string path, string root)
    {
        var folder = Path.GetDirectoryName(path);
        return folder is null ? "" : _projects.GetOrAdd(folder, f =>
        {
            for (var dir = f; dir is not null && dir.Length >= root.Length; dir = Path.GetDirectoryName(dir))
            {
                if (DirectoryListing.ProjectIn(dir) is { } project)
                {
                    return Path.GetFileNameWithoutExtension(project);
                }
            }

            return "";
        });
    }
}

/// <summary>A row of the code search: what matched, how, and the project it is in.</summary>
public sealed partial class CodeSearchResult(CodeMatch match, string project) : ObservableObject
{
    public CodeMatch Match { get; } = match;

    public CodeSymbol Symbol => Match.Symbol;

    public string Project { get; } = project;

    public string ToolTipText => Symbol.Kind == SymbolKind.File ? Symbol.Path : $"{Symbol.Path}:{Symbol.Line + 1}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
