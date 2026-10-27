using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.App.Infrastructure;
using Nomad.Core.Sessions;

namespace Nomad.App.ViewModels;

/// <summary>
/// The session search (Ctrl+T): titles and the conversations themselves, every session including archived ones,
/// optionally only the alive, idle or archived ones. Titles answer at once; the transcripts are read in the
/// background the first time (newest first), and the results grow as they come in.
/// </summary>
public sealed partial class SessionSearchViewModel : ObservableObject
{
    private const int MaxResults = 200;

    private readonly MainViewModel _main;
    private readonly TranscriptIndex _index = new();
    private readonly Dictionary<string, SessionSearchResult> _rows = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _progress;
    private CancellationTokenSource? _queryCts;
    private Task? _indexing;
    private int _indexed;
    private int _toIndex;
    private int _indexChanged;

    // Off after a new query or filter: the selection goes back to the top.
    private bool _keepSelection;

    public SessionSearchViewModel(MainViewModel main)
    {
        _main = main;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Refresh();
        };

        // While the transcripts are read: the count in the footer, and the query again as matches come in.
        _progress = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _progress.Tick += (_, _) => OnIndexProgress();
    }

    public ObservableCollection<SessionSearchResult> Results { get; } = [];

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

    /// <summary>"All", "Alive", "Idle" or "Archived".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll), nameof(IsFilterAlive), nameof(IsFilterIdle), nameof(IsFilterArchived))]
    public partial string Filter { get; set; } = "All";

    public bool IsFilterAll { get => Filter == "All"; set => SetFilter("All", value); }

    public bool IsFilterAlive { get => Filter == "Alive"; set => SetFilter("Alive", value); }

    public bool IsFilterIdle { get => Filter == "Idle"; set => SetFilter("Idle", value); }

    public bool IsFilterArchived { get => Filter == "Archived"; set => SetFilter("Archived", value); }

    private void SetFilter(string filter, bool on)
    {
        if (on)
        {
            Filter = filter;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected))]
    public partial int SelectedIndex { get; set; } = -1;

    public SessionSearchResult? Selected => SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    /// <summary>"Reading transcripts… 120 of 831" while they are read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    public partial string? Progress { get; set; }

    public bool HasProgress => Progress is not null;

    /// <summary>Nothing matches (shown in place of the list).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    partial void OnQueryChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    partial void OnFilterChanged(string value)
    {
        _keepSelection = false;
        Refresh();
    }

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

    public void Open(string? query = null)
    {
        if (query is not null)
        {
            Query = query;
        }

        IsOpen = true;
        IsShown = true;
        Dispatcher.UIThread.Post(() => IsRaised = IsOpen, DispatcherPriority.Loaded);
        _keepSelection = false;
        _debounce.Stop();
        Refresh();
        StartIndexing();
    }

    public void Close()
    {
        IsOpen = false;
        IsRaised = false;
        _debounce.Stop();
        _queryCts?.Cancel();
        DispatcherTimer.RunOnce(() => IsShown = IsOpen, TimeSpan.FromMilliseconds(220));
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

    /// <summary>Up or Down in the list (wrapping round).</summary>
    public void Move(int by)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = SelectedIndex < 0 ? (by > 0 ? 0 : Results.Count - 1) : SelectedIndex + by;
        SelectedIndex = by is 1 or -1 ? (index + Results.Count) % Results.Count : Math.Clamp(index, 0, Results.Count - 1);
    }

    /// <summary>Opens a result (the selected one if none is given) and closes the search.</summary>
    public void OpenResult(SessionSearchResult? result = null)
    {
        if ((result ?? Selected) is not { } row)
        {
            return;
        }

        Close();
        _ = _main.OpenSessionAsync(row.Item);
    }

    // ------------------------------------------------------------------ transcripts

    private void StartIndexing()
    {
        if (_indexing is { IsCompleted: false })
        {
            return;
        }

        // Newest first, so the sessions most likely looked for are searchable soonest.
        var files = _main.SearchableSessions().Select(s => s.FilePath).OfType<string>().ToList();
        _indexed = 0;
        _toIndex = files.Count;
        _indexing = Task.Run(() =>
        {
            var partitions = Partitioner.Create(files, EnumerablePartitionerOptions.NoBuffering);
            Parallel.ForEach(partitions, new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
            {
                if (_index.Update(file))
                {
                    Interlocked.Exchange(ref _indexChanged, 1);
                }

                Interlocked.Increment(ref _indexed);
            });
        });
        _progress.Start();
    }

    private void OnIndexProgress()
    {
        var done = _indexing?.IsCompleted != false;
        if (done)
        {
            _progress.Stop();
            Progress = null;
        }
        else if (_toIndex > 0 && Volatile.Read(ref _indexed) is var n && n < _toIndex)
        {
            // Only once it takes a moment (the first tick): a few changed files since last time go unmentioned.
            Progress = $"Reading transcripts… {n} of {_toIndex}";
        }

        if (Interlocked.Exchange(ref _indexChanged, 0) == 1 && IsOpen && Query.Trim().Length > 0)
        {
            Refresh();
        }
    }

    // ------------------------------------------------------------------ results

    private sealed record Candidate(SessionListItemViewModel Item, string Title, string? File, DateTime When);

    private sealed record Hit(SessionListItemViewModel Item, TextMatch? Title, TextMatch? Text);

    private void Refresh()
    {
        if (!IsOpen)
        {
            return;
        }

        _queryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        var terms = TranscriptIndex.Terms(Query.Trim());
        var candidates = _main.SearchableSessions()
            .Where(s => Filter switch
            {
                "Alive" => s.IsAlive,
                "Idle" => !s.IsAlive && !s.IsArchived,
                "Archived" => s.IsArchived,
                _ => true,
            })
            .Select(s => new Candidate(s, s.Title, s.FilePath, s.LastActivityUtc))
            .ToList();

        _ = Task.Run(() => Search(candidates, terms, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (!cts.IsCancellationRequested && t.IsCompletedSuccessfully)
            {
                Show(t.Result, terms.Length > 0);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private List<Hit> Search(List<Candidate> candidates, string[] terms, CancellationToken token)
    {
        if (terms.Length == 0)
        {
            return candidates.Take(MaxResults).Select(c => new Hit(c.Item, null, null)).ToList();
        }

        // Titles first, then conversations; newest first in each (the list already is).
        var titles = new List<Hit>();
        var texts = new List<Hit>();
        foreach (var c in candidates)
        {
            token.ThrowIfCancellationRequested();
            var title = TranscriptIndex.Find(c.Title, terms, before: 1000, after: 1000);
            if (title is not null && TranscriptIndex.ContainsAll(c.Title, terms))
            {
                titles.Add(new Hit(c.Item, title, null));
                continue;
            }

            if (texts.Count + titles.Count >= MaxResults || c.File is null || _index.TextOf(c.File) is not { } text)
            {
                continue;
            }

            // Words in the title count; the excerpt shows the first of the others.
            var rest = terms.Where(t => !c.Title.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (TranscriptIndex.ContainsAll(text, rest) && TranscriptIndex.Find(text, rest) is { } match)
            {
                texts.Add(new Hit(c.Item, title, match));
            }
        }

        return titles.Concat(texts).Take(MaxResults).ToList();
    }

    private void Show(List<Hit> hits, bool searching)
    {
        var keep = Selected?.Item;
        var list = new List<SessionSearchResult>(hits.Count);
        foreach (var hit in hits)
        {
            if (!_rows.TryGetValue(hit.Item.SessionId, out var row))
            {
                _rows[hit.Item.SessionId] = row = new SessionSearchResult(hit.Item);
            }

            row.Set(hit.Title, hit.Text);
            list.Add(row);
        }

        foreach (var row in Results)
        {
            row.IsSelected = false;
        }

        MainViewModel.SyncList(Results, list);
        // The same session stays selected as more matches arrive; a new query starts at the top.
        var index = keep is null ? -1 : list.FindIndex(r => r.Item == keep);
        SelectedIndex = -1;
        SelectedIndex = index >= 0 && _keepSelection ? index : list.Count > 0 ? 0 : -1;
        _keepSelection = true;
        IsEmpty = list.Count == 0;
        EmptyText = !searching ? "No sessions"
            : _indexing is { IsCompleted: false } ? "No matches yet: still reading transcripts"
            : "No sessions match";
    }

    partial void OnQueryChanging(string value) => _keepSelection = false;
}

/// <summary>A row of the session search: the session, where its title or conversation matched, its state and age.</summary>
public sealed partial class SessionSearchResult(SessionListItemViewModel item) : ObservableObject
{
    public SessionListItemViewModel Item { get; } = item;

    [ObservableProperty]
    public partial string TitleBefore { get; set; } = "";

    [ObservableProperty]
    public partial string TitleMatch { get; set; } = "";

    [ObservableProperty]
    public partial string TitleAfter { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnippet))]
    public partial string SnippetBefore { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnippet))]
    public partial string SnippetMatch { get; set; } = "";

    [ObservableProperty]
    public partial string SnippetAfter { get; set; } = "";

    public bool HasSnippet => SnippetMatch.Length > 0;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string When { get; set; } = "";

    [ObservableProperty]
    public partial string Folder { get; set; } = "";

    /// <summary>"Alive", "Idle" or "Archived".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlive), nameof(IsIdle), nameof(IsArchived))]
    public partial string State { get; set; } = "";

    public bool IsAlive => State == "Alive";

    public bool IsIdle => State == "Idle";

    public bool IsArchived => State == "Archived";

    public string ToolTipText => Item.ToolTipText;

    public void Set(TextMatch? title, TextMatch? text)
    {
        (TitleBefore, TitleMatch, TitleAfter) = title is null ? (Item.Title, "", "") : (title.Before, title.Match, title.After);
        (SnippetBefore, SnippetMatch, SnippetAfter) = text is null ? ("", "", "") : (text.Before, text.Match, text.After);
        When = Format.Since(Item.LastActivityUtc);
        Folder = Item.WorkingDirectory is null ? "No folder" : MainViewModel.ProjectName(Item.WorkingDirectory);
        State = Item.IsAlive ? "Alive" : Item.IsArchived ? "Archived" : "Idle";
    }
}
