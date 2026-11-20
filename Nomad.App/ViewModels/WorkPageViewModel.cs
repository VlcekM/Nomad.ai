using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Infrastructure;
using Nomad.Core.Agents;
using Nomad.Core.Git;

namespace Nomad.App.ViewModels;

/// <summary>
/// The pull requests and issues page: the open ones of every recent project, grouped by repository, each with buttons that
/// start a session on it in the background (the page stays open), on the model picked in its header.
/// </summary>
public sealed partial class WorkPageViewModel : ObservableObject
{
    /// <summary>
    /// The window, which owns the sessions and the settings.
    /// </summary>
    private readonly MainViewModel _main;

    /// <summary>
    /// The page of <paramref name="main"/>; nothing loads until it is first shown.
    /// </summary>
    public WorkPageViewModel(MainViewModel main)
    {
        _main = main;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => _ = LoadAsync(refresh: false);
    }

    #region Loading

    /// <summary>
    /// Refreshes the lists while the page is shown; stopped when it is not.
    /// </summary>
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// Counts loads, so an older one that finishes late doesn't overwrite a newer one's groups.
    /// </summary>
    private int _generation;

    /// <summary>
    /// The repositories answered so far, kept so a pin can reorder them without asking GitHub again.
    /// </summary>
    private List<RepoWork?> _answers = [];

    /// <summary>
    /// The repositories with something open, in the order of their folders (pinned first, then the latest).
    /// </summary>
    public ObservableCollection<WorkGroupViewModel> Groups { get; } = [];

    /// <summary>
    /// Some repository hasn't answered yet: the refresh button spins.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder), nameof(ShowNothing))]
    public partial bool IsLoading { get; set; }

    /// <summary>
    /// Every folder has been asked at least once.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder), nameof(ShowNothing))]
    public partial bool IsLoaded { get; set; }

    /// <summary>
    /// "Nothing open in Cloudovna.Web, Fis4": the repositories without open pull requests or issues, as one line.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuiet))]
    public partial string QuietText { get; set; } = "";

    /// <summary>
    /// Some repository has nothing open.
    /// </summary>
    public bool HasQuiet => QuietText.Length > 0;

    /// <summary>
    /// Some repository has something open.
    /// </summary>
    public bool HasGroups => Groups.Count > 0;

    /// <summary>
    /// Placeholder rows until the first repository answers.
    /// </summary>
    public bool ShowPlaceholder => !HasGroups && !HasQuiet && !IsLoaded;

    /// <summary>
    /// No project is a GitHub repository gh can read.
    /// </summary>
    public bool ShowNothing => IsLoaded && !HasGroups && !HasQuiet;

    /// <summary>
    /// The page came up: its lists load (from the minute-long cache when fresh) and refresh every minute while it stays.
    /// </summary>
    public void Show()
    {
        RebuildModels();
        Relink();
        _ = LoadAsync(refresh: false);
        _timer.Start();
    }

    /// <summary>
    /// The page went away: its refreshes stop.
    /// </summary>
    public void Hide() => _timer.Stop();

    /// <summary>
    /// The refresh button: asks GitHub again, whatever the cache holds.
    /// </summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(refresh: true);

    /// <summary>
    /// Asks every project's repository in parallel; each answer updates the groups as it comes.
    /// </summary>
    private async Task LoadAsync(bool refresh)
    {
        var generation = ++_generation;
        var folders = _main.WorkFolders();
        var lookups = folders.Select(f => GitHubWork.GetAsync(f, refresh)).ToList();
        IsLoading = true;

        var pending = lookups.Cast<Task>().ToList();
        while (pending.Count > 0)
        {
            pending.Remove(await Task.WhenAny(pending));
            if (generation != _generation) return;

            _answers = [.. lookups.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result)];
            Apply();
        }

        // No folder to ask (or a dev sample only): still show what there is.
        if (lookups.Count == 0) Apply();

        IsLoading = false;
        IsLoaded = true;
    }

    /// <summary>
    /// Pins a repository to the top of the page, or unpins it.
    /// </summary>
    [RelayCommand]
    private void TogglePin(WorkGroupViewModel group)
    {
        var pins = _main.Settings.PinnedRepos;
        if (PinKey(group.NameWithOwner) is { } key)
        {
            pins.Remove(key);
        }
        else
        {
            pins[group.NameWithOwner] = group.Folder;
        }

        _main.SaveSettings();
        Apply();
    }

    /// <summary>
    /// Shows the repositories answered so far: the pinned ones first by name (even with nothing open), then those with open
    /// items; the rest as one quiet line. Groups are kept, so their rows don't flicker.
    /// </summary>
    private void Apply()
    {
        var repos = GitHubWork.Group(_answers.Concat(Sample()));
        var pinned = repos.Where(IsPinned).OrderBy(r => Name(r), StringComparer.OrdinalIgnoreCase);
        var wanted = pinned.Concat(repos.Where(r => !IsPinned(r) && !r.IsEmpty)).Select(GroupFor).ToList();
        MainViewModel.SyncList(Groups, wanted);
        Relink();

        var quiet = repos.Where(r => r.IsEmpty && !IsPinned(r)).Select(Name).ToList();
        QuietText = quiet.Count == 0 ? "" : "Nothing open in " + string.Join(", ", quiet);
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(ShowNothing));
    }

    /// <summary>
    /// The group already shown for a repository (updated), or a new one.
    /// </summary>
    private WorkGroupViewModel GroupFor(RepoWork repo)
    {
        var group = Groups.FirstOrDefault(g => string.Equals(g.NameWithOwner, repo.NameWithOwner, StringComparison.OrdinalIgnoreCase))
            ?? new WorkGroupViewModel(repo.NameWithOwner, repo.Folder);
        group.IsPinned = IsPinned(repo);
        group.Update(repo);
        return group;
    }

    /// <summary>
    /// "Nomad.ai" from "VlcekM/Nomad.ai".
    /// </summary>
    private static string Name(RepoWork repo) => repo.NameWithOwner.Split('/')[^1];

    /// <summary>
    /// The repository is pinned to the top.
    /// </summary>
    private bool IsPinned(RepoWork repo) => PinKey(repo.NameWithOwner) is not null;

    /// <summary>
    /// The pin of a repository as saved (the settings' keys keep the case GitHub gave), or null when it isn't pinned.
    /// </summary>
    private string? PinKey(string nameWithOwner) =>
        _main.Settings.PinnedRepos.Keys.FirstOrDefault(k => string.Equals(k, nameWithOwner, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>--dev-work-sample</c>'s made-up repository: a pull request in every state and a couple of issues.
    /// </summary>
    private IEnumerable<RepoWork> Sample()
    {
        if (!_main.Dev.WorkSample) yield break;

        var now = DateTimeOffset.UtcNow;
        WorkItem Pr(int n, string title, string author, double hours, string[] labels, bool draft = false, string review = "") =>
            new(WorkKind.PullRequest, n, title, $"https://github.com/sample/Demo.App/pull/{n}", author, now.AddHours(-hours), labels, draft,
                $"pr-{n}", review);
        WorkItem Issue(int n, string title, string author, double hours, string[] labels) =>
            new(WorkKind.Issue, n, title, $"https://github.com/sample/Demo.App/issues/{n}", author, now.AddHours(-hours), labels);

        yield return new RepoWork("sample/Demo.App", Path.GetTempPath(),
        [
            Pr(42, "Add a dark theme to the settings page", "ann", 2, ["enhancement", "ui"], review: "APPROVED"),
            Pr(41, "Rewrite the sync engine", "bob", 26, [], draft: true),
            Pr(39, "Fix a crash when the project folder is removed", "cyril", 75, ["bug"], review: "CHANGES_REQUESTED"),
        ],
        [
            Issue(40, "Sign-in loops forever on Safari", "dana", 5, ["bug"]),
            Issue(37, "Export the report as CSV", "ann", 200, ["enhancement"]),
        ]);
    }

    #endregion

    #region Model

    /// <summary>
    /// The default agent's model chips: Claude's four, Codex's first four, or the starred OpenCode models.
    /// </summary>
    public ObservableCollection<MenuOption> Models { get; } = [];

    /// <summary>
    /// "Sessions start on Claude Code": which agent the chips belong to.
    /// </summary>
    [ObservableProperty]
    public partial string AgentText { get; set; } = "";

    /// <summary>
    /// The model a session starts with: the chip picked, else none (the agent's default).
    /// </summary>
    private string? SelectedModel => Models.FirstOrDefault(m => m.IsSelected)?.Id;

    /// <summary>
    /// A model chip was clicked: it is the one sessions start on from now on (remembered).
    /// </summary>
    [RelayCommand]
    private void SelectModel(MenuOption option)
    {
        foreach (var m in Models)
        {
            m.IsSelected = ReferenceEquals(m, option);
        }

        _main.Settings.WorkPageModel = option.Id;
        _main.SaveSettings();
    }

    /// <summary>
    /// Fills the chips for the default agent, the saved pick selected (else the agent's default model). Codex and OpenCode
    /// list their models from their servers: the chips follow once those are read.
    /// </summary>
    private void RebuildModels()
    {
        var backend = AgentBackends.Normalize(_main.Settings.DefaultBackend);
        AgentText = $"Sessions start on {AgentBackends.Name(backend)}";
        FillModels(backend);

        var load = backend switch
        {
            AgentBackends.Codex when CodexCatalog.Models.Count == 0 => _main.LoadCodexCatalogAsync(),
            AgentBackends.OpenCode when OpenCodeCatalog.Models.Count == 0 => _main.LoadOpenCodeCatalogAsync(),
            _ => null,
        };
        load?.ContinueWith(_ => FillModels(backend), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// The chips of <paramref name="backend"/>, the agent's default model added when none of them is it.
    /// </summary>
    private void FillModels(string backend)
    {
        var ids = ModelIds(backend);
        var fallback = DefaultModel(backend);
        if (!string.IsNullOrEmpty(fallback) && !ids.Any(id => Same(id, fallback))) ids.Add(fallback);

        var saved = _main.Settings.WorkPageModel;
        var selected = ids.FirstOrDefault(id => Same(id, saved)) ?? ids.FirstOrDefault(id => Same(id, fallback)) ?? ids.FirstOrDefault();
        Models.Clear();
        foreach (var id in ids)
        {
            Models.Add(new MenuOption(id, Catalog.ModelName(id)) { IsSelected = id == selected });
        }
    }

    /// <summary>
    /// The models the status row offers as chips for <paramref name="backend"/>.
    /// </summary>
    private List<string> ModelIds(string backend) => backend switch
    {
        AgentBackends.OpenCode => [.. _main.Settings.OpenCodeFavorites],
        AgentBackends.Codex => [.. CodexCatalog.Models.Take(4).Select(m => m.Id)],
        AgentBackends.Antigravity => [.. Catalog.AntigravityModels.Select(m => m.Id)],
        _ => [.. Catalog.Models.Select(m => m.Id)],
    };

    /// <summary>
    /// The model a new session on <paramref name="backend"/> starts with.
    /// </summary>
    private string? DefaultModel(string backend) => backend switch
    {
        AgentBackends.OpenCode => _main.Settings.OpenCodeModel ?? OpenCodeCatalog.DefaultModel,
        AgentBackends.Codex => _main.Settings.CodexModel ?? CodexCatalog.DefaultModel,
        AgentBackends.Antigravity => Nomad.Core.Antigravity.AntigravityBackend.DefaultModel,
        _ => _main.DefaultModel,
    };

    /// <summary>
    /// The same model; Claude's ids may carry a suffix ("[1m]") the chip's lacks.
    /// </summary>
    private static bool Same(string id, string? other) =>
        other is not null && (string.Equals(id, other, StringComparison.OrdinalIgnoreCase)
            || (other.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) && other.StartsWith(id + "[", StringComparison.OrdinalIgnoreCase)));

    #endregion

    #region Sessions

    /// <summary>
    /// The sessions started here, by the URL they work on: their row's chip shows them before the session list does.
    /// </summary>
    private readonly Dictionary<string, SessionViewModel> _started = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ctrl was held when the page was last pressed (the view records it before the click): the session starts in a new
    /// worktree rather than in the project's own checkout.
    /// </summary>
    public bool CtrlClick { get; set; }

    /// <summary>
    /// Ctrl is held: the row buttons and the header's hint say a click starts in a new worktree.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorktreeHint))]
    public partial bool IsCtrlHeld { get; set; }

    /// <summary>
    /// The header's hint: how to start in a new worktree, or that a click now does.
    /// </summary>
    public string WorktreeHint => IsCtrlHeld ? "Starts in a new worktree" : "Ctrl+click to start in a new worktree";

    /// <summary>
    /// Fixes an issue, in the project's checkout (Ctrl+click: a new worktree).
    /// </summary>
    [RelayCommand]
    private Task FixAsync(WorkRowViewModel row) => StartAsync(row, $"Fix this issue: {row.Url}", worktree: CtrlClick);

    /// <summary>
    /// Reviews a pull request, in the project's checkout (Ctrl+click: a new worktree, the checkout left alone).
    /// </summary>
    [RelayCommand]
    private Task ReviewAsync(WorkRowViewModel row) => StartAsync(row, $"Review this pull request: {row.Url}", worktree: CtrlClick);

    /// <summary>
    /// Merges a pull request, the agent reconciling it with the repository (conflicts, its style) as it would when asked in a
    /// session; in the project's checkout (Ctrl+click: a new worktree).
    /// </summary>
    [RelayCommand]
    private Task MergeAsync(WorkRowViewModel row) => StartAsync(row, $"Merge this pull request: {row.Url}", worktree: CtrlClick);

    /// <summary>
    /// The chip: shows the row's session in place of the page.
    /// </summary>
    [RelayCommand]
    private async Task OpenSessionAsync(WorkRowViewModel row)
    {
        if (row.Session is { } listed)
        {
            await _main.OpenSessionAsync(listed);
            return;
        }

        if (row.Starting is { } session) _main.ShowSession(session);
    }

    /// <summary>
    /// Opens a pull request, an issue or a repository on GitHub.
    /// </summary>
    [RelayCommand]
    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser registered.
        }
    }

    /// <summary>
    /// Starts the session in the background; why it couldn't (git failed to make the worktree) shows under the row.
    /// </summary>
    private async Task StartAsync(WorkRowViewModel row, string prompt, bool worktree)
    {
        if (row.HasSession || Groups.FirstOrDefault(g => g.PullRequests.Contains(row) || g.Issues.Contains(row)) is not { } group) return;

        row.Error = "";
        row.IsLaunching = true;
        try
        {
            _started[row.Url] = await _main.StartBackgroundSessionAsync(group.Folder, prompt, SelectedModel, worktree);
        }
        catch (Exception ex)
        {
            row.Error = ex.Message;
        }
        finally
        {
            row.IsLaunching = false;
        }

        Link(row);
    }

    /// <summary>
    /// The session list changed (a session got its id, a rescan): every row's chip follows.
    /// </summary>
    public void Relink()
    {
        foreach (var row in Groups.SelectMany(g => g.PullRequests.Concat(g.Issues)))
        {
            Link(row);
        }
    }

    /// <summary>
    /// Finds the row's session: listed if it is, else the one started here, still without an id.
    /// </summary>
    private void Link(WorkRowViewModel row)
    {
        _started.TryGetValue(row.Url, out var started);
        row.Session = _main.SessionRowFor(row.Url, started);
        row.Starting = row.Session is null ? started : null;
    }

    #endregion
}
