using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Core.Git;

namespace Nomad.App.ViewModels;

/// <summary>
/// The pull requests and issues page: shown in place of the session, and the sessions it starts in the background.
/// </summary>
public sealed partial class MainViewModel
{
    #region The page

    /// <summary>
    /// The page's lists and model chips, kept while it is closed so it comes back as it was.
    /// </summary>
    public WorkPageViewModel WorkPage => field ??= new WorkPageViewModel(this);

    /// <summary>
    /// The page is shown in place of the session (the sidebar's "Pull requests & issues", or the new-session page's button).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSidePanel), nameof(IsNewSelected), nameof(ShowSessionChrome))]
    public partial bool IsWorkPageOpen { get; set; }

    /// <summary>
    /// The title bar's session parts (title, branch, side panel and Files switch): hidden while the page is shown.
    /// </summary>
    public bool ShowSessionChrome => !IsWorkPageOpen;

    /// <summary>
    /// Shows the page in place of the session.
    /// </summary>
    [RelayCommand]
    public void OpenWorkPage() => IsWorkPageOpen = true;

    /// <summary>
    /// Takes the page away, back to the session.
    /// </summary>
    [RelayCommand]
    public void CloseWorkPage() => IsWorkPageOpen = false;

    /// <summary>
    /// The page loads and refreshes only while it is shown.
    /// </summary>
    partial void OnIsWorkPageOpenChanged(bool value)
    {
        if (value)
        {
            WorkPage.Show();
        }
        else
        {
            WorkPage.Hide();
        }
    }

    /// <summary>
    /// The folders the page lists: the pinned repositories' checkouts, then the projects pinned and latest on the new-session page.
    /// </summary>
    public IReadOnlyList<string> WorkFolders()
    {
        var pinnedRepos = Settings.PinnedRepos.Values.Where(Directory.Exists);
        var pinned = Settings.PinnedFolders.Where(Directory.Exists);
        return [.. pinnedRepos.Concat(pinned).Concat(RecentFolders).DistinctBy(f => f.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)];
    }

    #endregion

    #region Background sessions

    /// <summary>
    /// Sessions started in the background that have no id yet: counted as open (quitting waits for their turns) until
    /// <see cref="OnSessionIdentified"/> lists them.
    /// </summary>
    private readonly List<SessionViewModel> _starting = [];

    /// <summary>
    /// Starts a session on the default agent in <paramref name="folder"/> (or a new worktree of it) and sends it
    /// <paramref name="prompt"/>, without showing it: it joins the Alive list and runs as any other. Throws when git can't
    /// make the worktree.
    /// </summary>
    public async Task<SessionViewModel> StartBackgroundSessionAsync(string folder, string prompt, string? model, bool worktree)
    {
        var workFolder = worktree ? await GitInfo.AddWorktreeAsync(folder) : folder;
        var session = new SessionViewModel(this, workFolder) { StartsInPlace = true };
        if (!string.IsNullOrEmpty(model))
        {
            session.Model = model;
            session.Effort = EffortFor(model);
        }

        _starting.Add(session);
        _ = session.SendPromptAsync(prompt);
        return session;
    }

    /// <summary>
    /// Starts a side task Claude suggested in <paramref name="parent"/> as a chat of its own, on the same agent, model and mode,
    /// and sends it <paramref name="prompt"/> without showing it: the sidebar lists it under its parent.
    /// </summary>
    public SessionViewModel StartForkedSession(SessionViewModel parent, string prompt, string toolUseId)
    {
        // In its project itself, not in the parent's worktree: a new one, if the Worktree switch says so, comes from there.
        var folder = parent.Folder is { } own ? GitInfo.WorktreeOwner(own) ?? own : null;
        var session = new SessionViewModel(this, folder, parent.Backend)
        {
            ForkedFrom = parent.SessionId,
            ForkedFromCall = toolUseId,
            Model = parent.Model,
            Effort = parent.Effort,
            PermissionMode = parent.PermissionMode,
        };
        _starting.Add(session);
        _ = session.SendPromptAsync(prompt);
        return session;
    }

    /// <summary>
    /// Opens the listed session <paramref name="sessionId"/>; false when it isn't listed (deleted, say).
    /// </summary>
    public bool OpenListedSession(string sessionId)
    {
        if (_all.FirstOrDefault(s => s.SessionId == sessionId) is not { } row) return false;

        IsWorkPageOpen = false;
        _ = OpenSessionAsync(row);
        return true;
    }

    /// <summary>
    /// Shows a session the page started that isn't listed yet.
    /// </summary>
    public void ShowSession(SessionViewModel session)
    {
        IsWorkPageOpen = false;
        Current = session;
    }

    /// <summary>
    /// The listed session working on a pull request or issue: the one the page started for it, else the latest whose
    /// transcript or branch links it.
    /// </summary>
    public SessionListItemViewModel? SessionRowFor(string url, SessionViewModel? started)
    {
        if (started?.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } own) return own;

        return _all.FirstOrDefault(s => !s.IsArchived && Links(s, url));
    }

    /// <summary>
    /// The session row names the pull request or issue: as what its transcript mentions, or what its branch is for.
    /// </summary>
    private static bool Links(SessionListItemViewModel row, string url) =>
        Same(row.PullRequestUrl, url) || Same(row.IssueUrl, url) || Same(row.PullRequest?.Url, url) || Same(row.Issue?.Url, url);

    /// <summary>
    /// The same URL, whatever the case.
    /// </summary>
    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    #endregion
}
