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

    public void Open()
    {
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
        if ((result ?? Selected) is not { } row)
        {
            return;
        }

        Close();
        var symbol = row.Symbol;
        files.OpenAt(symbol.Path, symbol.Kind == SymbolKind.File ? null : (symbol.Line, symbol.Column));
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
