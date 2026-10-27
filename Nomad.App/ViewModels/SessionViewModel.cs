using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Infrastructure;
using Nomad.Core.Agents;
using Nomad.Core.Git;
using Nomad.Core.Sessions;

namespace Nomad.App.ViewModels;

public enum AgentState
{
    Idle,
    Running,
    Done,
    Error,
    Interrupted,
}

/// <summary>
/// One session: its transcript, composer, status row and (when running) its agent process.
/// All members are used on the UI thread; agent events are marshalled here.
/// </summary>
public sealed partial class SessionViewModel : ObservableObject
{
    private const int MaxResultChars = 20_000;

    private readonly MainViewModel _main;
    private readonly Dictionary<string, ToolCallItem> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolGroupItem> _groupOfCall = new(StringComparer.Ordinal);
    private readonly Dictionary<(string MessageId, int Index), TranscriptItem> _blocks = [];
    private IAgentBackend? _backend;
    private ToolGroupItem? _openGroup;
    private bool _restartRequired;

    // Rewind / fork: where the next process resumes (null: the whole transcript), and from which
    // session a fork starts. Kept until a prompt went out in a process started with them.
    private string? _resumeAt;
    private string? _forkFrom;
    private UserMessageItem? _unseen;
    private bool _suppressModeSync;
    private int _turnToolCount;

    public SessionViewModel(MainViewModel main, string? folder)
    {
        _main = main;
        Folder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        Model = main.DefaultModel;
        PermissionMode = main.Settings.DefaultPermissionMode;
        Effort = main.EffortFor(Model);
        IsRemoteControl = main.Settings.RemoteControlByDefault;

        foreach (var m in Catalog.Models)
        {
            ModelOptions.Add(new MenuOption(m.Id, m.DisplayName, shortcut: m.Shortcut));
        }

        foreach (var m in Catalog.Modes)
        {
            ModeOptions.Add(new MenuOption(m.Id, m.DisplayName, m.Description, m.Shortcut) { IsDanger = m.Id == "bypassPermissions" });
        }

        SyncOptions();
        RefreshFolders();
    }

    public ObservableCollection<TranscriptItem> Items { get; } = [];

    // ------------------------------------------------------------------ identity

    [ObservableProperty]
    public partial string? SessionId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleOrPlaceholder))]
    public partial string Title { get; set; } = "";

    /// <summary>
    /// The name given with Rename while this window ran the session: written again when its CLI exits, since the
    /// CLI re-appends the title it knew to the transcript then.
    /// </summary>
    public string? RenamedTitle { get; set; }

    /// <summary>No prompt sent and no history: the new-session view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerPlaceholder))]
    [NotifyPropertyChangedFor(nameof(TitleOrPlaceholder))]
    [NotifyPropertyChangedFor(nameof(HasTitle))]
    [NotifyPropertyChangedFor(nameof(ShowWorktreeToggle))]
    [NotifyPropertyChangedFor(nameof(GitChipText))]
    [NotifyPropertyChangedFor(nameof(GitChipToolTip))]
    [NotifyPropertyChangedFor(nameof(ShowTitleGit))]
    [NotifyPropertyChangedFor(nameof(IsWorktreeSession))]
    public partial bool IsNew { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorktreeSession))]
    [NotifyPropertyChangedFor(nameof(WorktreeTitleToolTip))]
    [NotifyPropertyChangedFor(nameof(FolderName))]
    [NotifyPropertyChangedFor(nameof(HasFolder))]
    [NotifyPropertyChangedFor(nameof(FolderToolTip))]
    public partial string? Folder { get; set; }

    public string FolderName => MainViewModel.ProjectName(Folder);

    /// <summary>The folder is in a git repository: the git chip shows, and a new session can start in a worktree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWorktreeToggle))]
    [NotifyPropertyChangedFor(nameof(ShowTitleGit))]
    public partial bool IsGitFolder { get; set; }

    public bool ShowWorktreeToggle => IsNew && IsGitFolder;

    /// <summary>A started session shows its branch (and worktree, if any) in the title bar instead of the status row.</summary>
    public bool ShowTitleGit => !IsNew && IsGitFolder;

    /// <summary>The session runs in a worktree of its repository (<c>.claude\worktrees</c>).</summary>
    public bool IsWorktreeSession => !IsNew && Folder is not null && GitInfo.WorktreeOwner(Folder) is not null;

    public string WorktreeTitleToolTip => $"Worktree {Folder}";

    /// <summary>The folder's checked-out branch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitChipText))]
    public partial string GitBranch { get; set; } = "";

    /// <summary>A new session's worktree starts from this branch (null: the one checked out).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitChipText))]
    public partial string? WorktreeFrom { get; set; }

    /// <summary>
    /// The git chip: a new session shows the branch it starts on (with Worktree on, the one the worktree starts from);
    /// a running one its worktree, if any, and branch.
    /// </summary>
    public string GitChipText =>
        IsNew ? (UseWorktree ? WorktreeFrom ?? GitBranch : GitBranch)
        : Folder is not null && GitInfo.WorktreeOwner(Folder) is not null ? $"worktree · {GitBranch}"
        : GitBranch;

    /// <summary>The branch the git chip is about: the worktree's start for a new session with Worktree on, else the checked-out one.</summary>
    private string ChipBranch => IsNew && UseWorktree ? WorktreeFrom ?? GitBranch : GitBranch;

    public string GitChipToolTip =>
        IsNew ? (UseWorktree ? "The branch the new worktree starts from" : "Switch branch")
        : Folder is not null && GitInfo.WorktreeOwner(Folder) is not null ? $"Worktree {Folder}, branch {GitBranch}"
        : $"Branch {GitBranch}";

    /// <summary>The branch menu: the local branches, newest first, filtered by <see cref="BranchFilter"/>.</summary>
    public ObservableCollection<MenuOption> MenuBranches { get; } = [];

    private IReadOnlyList<string> _branches = [];

    [ObservableProperty]
    public partial string BranchFilter { get; set; } = "";

    /// <summary>Why the last branch switch failed (shown in the branch menu).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBranchError))]
    public partial string BranchError { get; set; } = "";

    public bool HasBranchError => BranchError.Length > 0;

    partial void OnBranchFilterChanged(string value) => FillBranches();

    /// <summary>Reads the branches for the branch menu.</summary>
    public async Task LoadBranchesAsync()
    {
        BranchError = "";
        BranchFilter = "";
        _branches = Folder is { } folder ? await GitInfo.BranchesAsync(folder) : [];
        FillBranches();
    }

    private void FillBranches()
    {
        var selected = UseWorktree ? WorktreeFrom ?? GitBranch : GitBranch;
        var query = BranchFilter.Trim();
        MenuBranches.Clear();
        foreach (var b in _branches.Where(b => query.Length == 0 || b.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(30))
        {
            MenuBranches.Add(new MenuOption(b, b) { IsSelected = b == selected });
        }
    }

    /// <summary>
    /// A branch picked in the menu: with Worktree on, the new worktree starts from it (the folder stays as it is);
    /// otherwise the folder switches to it. True when done; false leaves git's reason in <see cref="BranchError"/>.
    /// </summary>
    public async Task<bool> PickBranchAsync(string branch)
    {
        if (!IsNew || Folder is not { } folder)
        {
            return false;
        }

        if (UseWorktree)
        {
            WorktreeFrom = branch == GitBranch ? null : branch;
            OnPropertyChanged(nameof(GitChipToolTip));
            return true;
        }

        if (branch == GitBranch)
        {
            return true;
        }

        if (await GitInfo.SwitchBranchAsync(folder, branch) is { } error)
        {
            BranchError = error;
            return false;
        }

        await RefreshGitAsync();
        _ = _main.ProjectFor(folder).LoadAsync(refresh: true);
        return true;
    }

    /// <summary>Reads the folder's branch again (on a folder change, and after each turn: Claude may switch it).</summary>
    public async Task RefreshGitAsync()
    {
        var folder = Folder;
        var status = folder is null ? null : await GitInfo.GetAsync(folder, 1, refresh: true);
        if (folder != Folder)
        {
            return;
        }

        IsGitFolder = status is not null;
        GitChanges = status?.Changes ?? 0;
        GitBranch = status?.Branch ?? "";
        OnPropertyChanged(nameof(GitChipToolTip));
        await RefreshAheadAsync();
    }

    /// <summary>Commits on the chip's branch not pushed to its upstream yet (shown as ↑N next to it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitAhead))]
    [NotifyPropertyChangedFor(nameof(GitAheadToolTip))]
    public partial int GitAhead { get; set; }

    public bool HasGitAhead => GitAhead > 0;

    public string GitAheadToolTip => GitAhead == 1 ? "1 commit not pushed yet" : $"{GitAhead} commits not pushed yet";

    /// <summary>Files changed in the folder's checkout and not committed (shown as "N changed" after the branch).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitChanges))]
    [NotifyPropertyChangedFor(nameof(GitChangesText))]
    [NotifyPropertyChangedFor(nameof(GitChangesToolTip))]
    public partial int GitChanges { get; set; }

    public bool HasGitChanges => GitChanges > 0;

    public string GitChangesText => $"{GitChanges} changed";

    public string GitChangesToolTip => GitChanges == 1 ? "1 file with uncommitted changes" : $"{GitChanges} files with uncommitted changes";

    private async Task RefreshAheadAsync()
    {
        var folder = Folder;
        var branch = ChipBranch;
        var ahead = folder is null || branch.Length == 0 || branch == "detached" ? 0 : await GitInfo.AheadAsync(folder, branch);
        if (folder == Folder && branch == ChipBranch)
        {
            GitAhead = ahead;
        }
    }

    partial void OnWorktreeFromChanged(string? value) => _ = RefreshAheadAsync();

    /// <summary>Start the new session in a new worktree of the folder's repository (kept for the next ones too).</summary>
    public bool UseWorktree
    {
        get => _main.Settings.NewSessionWorktree;
        set
        {
            if (_main.Settings.NewSessionWorktree != value)
            {
                _main.Settings.NewSessionWorktree = value;
                _main.SaveSettings();
                OnPropertyChanged();
                OnPropertyChanged(nameof(GitChipText));
                OnPropertyChanged(nameof(GitChipToolTip));
                OnPropertyChanged(nameof(BranchMenuHeader));
                OnPropertyChanged(nameof(WorktreeToolTip));
                _ = RefreshAheadAsync();
            }
        }
    }

    public string WorktreeToolTip => UseWorktree
        ? "Worktree on: starts in a new git worktree (.claude\\worktrees) on its own branch"
        : "Worktree off: starts in the folder itself";

    public string BranchMenuHeader => UseWorktree ? "Worktree starts from" : "Switch branch";

    public bool HasFolder => !string.IsNullOrEmpty(Folder);

    public string FolderToolTip => Folder ?? "Claude works in a new scratch folder";

    public bool HasTitle => !IsNew && !string.IsNullOrEmpty(Title);

    public string TitleOrPlaceholder => IsNew || string.IsNullOrEmpty(Title) ? "New session" : Title;

    // ------------------------------------------------------------------ configuration

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelName))]
    [NotifyPropertyChangedFor(nameof(AgentMetaLine))]
    public partial string Model { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeName))]
    [NotifyPropertyChangedFor(nameof(IsBypass))]
    public partial string PermissionMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffortName))]
    [NotifyPropertyChangedFor(nameof(EffortLabel))]
    [NotifyPropertyChangedFor(nameof(HasEffort))]
    public partial string Effort { get; set; }

    [ObservableProperty]
    public partial bool FastMode { get; set; }

    /// <summary>Remote Control is wanted: the running process is reachable from claude.ai and the phone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteControlText))]
    public partial bool IsRemoteControl { get; set; }

    /// <summary>The session's claude.ai link while Remote Control is on and a process runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoteControlUrl))]
    [NotifyPropertyChangedFor(nameof(RemoteControlText))]
    public partial string? RemoteControlUrl { get; set; }

    /// <summary>The bridge's state: connected | reconnecting | failed | … (empty without a bridge).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoteConnected))]
    [NotifyPropertyChangedFor(nameof(RemoteControlText))]
    public partial string RemoteControlState { get; set; } = "";

    public bool HasRemoteControlUrl => !string.IsNullOrEmpty(RemoteControlUrl);

    public bool IsRemoteConnected => HasRemoteControlUrl && RemoteControlState is "connected" or "ready" or "";

    /// <summary>The Remote Control menu's status line.</summary>
    public string RemoteControlText => !IsRemoteControl
        ? "Off. This session is only in Nomad."
        : HasRemoteControlUrl
            ? RemoteControlState switch
            {
                "reconnecting" => "Reconnecting…",
                "failed" or "disconnected" => "Not connected",
                _ => "On. Continue it in the Claude app or on claude.ai.",
            }
            : IsNew
                ? "Turns on when the session starts."
                : "Turns on while Claude Code runs for this session.";

    public string ModelName => Catalog.ModelName(Model);

    public string ModeName => Catalog.ModeName(PermissionMode);

    public bool IsBypass => PermissionMode == "bypassPermissions";

    public string EffortName => string.IsNullOrEmpty(Effort) ? "" : Catalog.EffortName(Effort);

    public bool HasEffort => !string.IsNullOrEmpty(Effort);

    /// <summary>The effort row of the model menu ("Default" when the CLI decides).</summary>
    public string EffortLabel => Catalog.EffortName(Effort);

    // ------------------------------------------------------------------ menus

    public ObservableCollection<MenuOption> ModelOptions { get; } = [];

    public ObservableCollection<MenuOption> ModeOptions { get; } = [];

    /// <summary>
    /// The effort slider's thumb, 0 (Low) to 4 (Max). It moves freely while dragged; the nearest stop
    /// becomes the effort, and <see cref="SnapEffort"/> settles the thumb on it when released.
    /// "Default" (no effort flag) sits at High, which is what the CLI picks.
    /// </summary>
    public double EffortPosition
    {
        get => _effortPosition;
        set
        {
            if (SetProperty(ref _effortPosition, value))
            {
                var id = Catalog.EffortLevels[Math.Clamp((int)Math.Round(value), 0, Catalog.EffortLevels.Count - 1)].Id;
                if (id != Effort)
                {
                    SelectEffortCommand.Execute(id);
                }
            }
        }
    }

    private double _effortPosition;

    public void SnapEffort() => EffortPosition = Math.Round(EffortPosition);

    private static double EffortIndex(string? id)
    {
        var index = Catalog.EffortLevels.ToList().FindIndex(e => e.Id == id);
        return index >= 0 ? index : Catalog.EffortLevels.ToList().FindIndex(e => e.Id == "high");
    }

    /// <summary>Recent folders for the new-session card (up to five).</summary>
    public ObservableCollection<MenuOption> CardFolders { get; } = [];

    /// <summary>The new-session page's project cards: the latest folders with their git state.</summary>
    public ObservableCollection<ProjectViewModel> Projects { get; } = [];

    public bool HasProjects => Projects.Count > 0;

    /// <summary>Recent folders for the folder menu, filtered by <see cref="FolderFilter"/>.</summary>
    public ObservableCollection<MenuOption> MenuFolders { get; } = [];

    public bool HasCardFolders => CardFolders.Count > 0;

    [ObservableProperty]
    public partial string FolderFilter { get; set; } = "";

    public bool IsNoFolder => Folder is null;

    partial void OnModelChanged(string value) => SyncOptions();

    partial void OnPermissionModeChanged(string value) => SyncOptions();

    partial void OnEffortChanged(string value)
    {
        SyncOptions();

        // Changed elsewhere (settings, a restored session): move the thumb, unless a drag already points there.
        var index = EffortIndex(value);
        if (Math.Round(_effortPosition) != index || string.IsNullOrEmpty(value))
        {
            _effortPosition = index;
            OnPropertyChanged(nameof(EffortPosition));
        }
    }

    partial void OnFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(IsNoFolder));
        SyncFolderSelection();
        if (IsNew)
        {
            WorktreeFrom = null;
        }

        _ = RefreshGitAsync();

        if (_main.IsSidePanelOpen)
        {
            RefreshPanel();
        }
    }

    partial void OnFolderFilterChanged(string value) => RefreshFolders();

    private void SyncOptions()
    {
        foreach (var o in ModelOptions)
        {
            o.IsSelected = string.Equals(o.Id, Model, StringComparison.OrdinalIgnoreCase)
                || (Model?.StartsWith(o.Id, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        foreach (var o in ModeOptions)
        {
            o.IsSelected = o.Id == PermissionMode;
        }
    }

    /// <summary>Rebuilds the folder lists from the app's recent folders.</summary>
    public void RefreshFolders()
    {
        var all = _main.RecentFolders;
        Fill(CardFolders, all.Take(5));
        var query = FolderFilter.Trim();
        Fill(MenuFolders, (query.Length == 0 ? all : all.Where(f => f.Contains(query, StringComparison.OrdinalIgnoreCase))).Take(10));
        if (IsNew)
        {
            RefreshProjects(all);
        }

        SyncFolderSelection();
        OnPropertyChanged(nameof(HasCardFolders));

        static void Fill(ObservableCollection<MenuOption> target, IEnumerable<string> folders)
        {
            target.Clear();
            foreach (var f in folders)
            {
                target.Add(new MenuOption(f, Format.FolderName(f), toolTip: f));
            }
        }
    }

    private void SyncFolderSelection()
    {
        foreach (var o in CardFolders.Concat(MenuFolders))
        {
            o.IsSelected = IsFolder(o.Id);
        }

        foreach (var p in Projects)
        {
            p.IsSelected = IsFolder(p.Path);
        }
    }

    private bool IsFolder(string path) =>
        Folder is not null && string.Equals(path.TrimEnd('\\', '/'), Folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Pins a project card to the top of the new-session page, or unpins it.</summary>
    public void TogglePin(ProjectViewModel project)
    {
        var pinned = _main.Settings.PinnedFolders;
        var key = project.Path.TrimEnd('\\', '/');
        if (pinned.RemoveAll(f => string.Equals(f.TrimEnd('\\', '/'), key, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            pinned.Add(project.Path);
        }

        _main.SaveSettings();
        RefreshProjects(_main.RecentFolders);
    }

    /// <summary>
    /// The project cards (shared with the app, so their git state is kept), refreshed: the pinned folders first,
    /// then the latest ones up to six cards in all.
    /// </summary>
    private void RefreshProjects(IReadOnlyList<string> folders)
    {
        var pinned = _main.Settings.PinnedFolders.Where(Directory.Exists).ToList();
        bool IsPinned(string f) => pinned.Any(p => string.Equals(p.TrimEnd('\\', '/'), f.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        var recent = folders.Where(f => !IsPinned(f)).Take(Math.Max(0, 6 - pinned.Count));
        var wanted = pinned.Concat(recent).Select(_main.ProjectFor).ToList();
        foreach (var p in wanted)
        {
            p.IsPinned = IsPinned(p.Path);
        }

        if (!wanted.SequenceEqual(Projects))
        {
            Projects.Clear();
            foreach (var p in wanted)
            {
                Projects.Add(p);
            }

            OnPropertyChanged(nameof(HasProjects));
        }

        foreach (var p in Projects)
        {
            p.LastSessionText = _main.LastActivityIn(p.Path) is { } last ? "Last session " + Format.Ago(last) : "";
            _ = p.LoadAsync();
        }
    }

    // ------------------------------------------------------------------ subagents

    /// <summary>The session's subagents (Agent / Task calls), newest first: the side panel lists them.</summary>
    public ObservableCollection<SubagentViewModel> Subagents { get; } = [];

    public bool HasSubagents => Subagents.Count > 0;

    private readonly Dictionary<string, SubagentViewModel> _subagentById = new(StringComparer.Ordinal);
    private DispatcherTimer? _subagentTimer;

    /// <summary>The subagent whose conversation is open over the transcript.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSubagent))]
    public partial SubagentViewModel? OpenSubagent { get; set; }

    public bool HasOpenSubagent => OpenSubagent is not null;

    private void StartSubagent(string toolUseId, System.Text.Json.JsonElement input)
    {
        if (!_subagentById.TryGetValue(toolUseId, out var agent))
        {
            agent = new SubagentViewModel(toolUseId, Folder);
            _subagentById[toolUseId] = agent;
            Subagents.Insert(0, agent);
            OnPropertyChanged(nameof(HasSubagents));
        }

        agent.SetInput(input);

        // Durations tick once a second, only while one runs.
        _subagentTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickSubagents());
        _subagentTimer.Start();
    }

    private void TickSubagents()
    {
        var running = false;
        foreach (var agent in Subagents.Where(a => a.IsRunning))
        {
            agent.Tick();
            running = true;
        }

        if (!running)
        {
            _subagentTimer?.Stop();
        }
    }

    /// <summary>The CLI's events from inside a subagent (they carry its Agent call's id as their parent).</summary>
    private bool HandleSubagentEvent(AgentEvent e)
    {
        switch (e)
        {
            case AssistantMessageStartedEvent { ParentToolUseId: { } parent } m when _subagentById.TryGetValue(parent, out var agent):
                agent.SetModel(m.Model);
                return true;
            case TextDeltaEvent { ParentToolUseId: { } parent } t when _subagentById.TryGetValue(parent, out var agent):
                agent.AppendText(t.MessageId, t.Index, t.Text);
                return true;
            case BlockCompletedEvent { ParentToolUseId: { } parent, Kind: AgentBlockKind.Text } c when _subagentById.TryGetValue(parent, out var agent):
                agent.CompleteText(c.MessageId, c.Index, c.Text);
                return true;
            case ToolCallEvent { ParentToolUseId: { } parent } call when _subagentById.TryGetValue(parent, out var agent):
                agent.AddCall(call.ToolUseId, call.Name, call.Input);
                return true;
            case ToolResultEvent { ParentToolUseId: { } parent } r when _subagentById.TryGetValue(parent, out var agent):
                agent.CompleteCall(r.ToolUseId, r.IsError, r.Text);
                return true;
            default:
                return false;
        }
    }

    private void FinishSubagents(bool includingBackground)
    {
        foreach (var agent in Subagents.Where(a => a.IsRunning && (includingBackground || !a.IsBackground)))
        {
            agent.Finish(failed: false, result: null);
        }
    }

    // ------------------------------------------------------------------ background tasks

    /// <summary>Commands started with <c>run_in_background</c>, newest first: the side panel lists them.</summary>
    public ObservableCollection<BackgroundTaskViewModel> BackgroundTasks { get; } = [];

    public bool HasBackgroundTasks => BackgroundTasks.Count > 0;

    private readonly Dictionary<string, BackgroundTaskViewModel> _backgroundTaskById = new(StringComparer.Ordinal);
    private DispatcherTimer? _backgroundTaskTimer;

    private void StartBackgroundTask(string toolUseId, string toolName, System.Text.Json.JsonElement input)
    {
        if (_backgroundTaskById.ContainsKey(toolUseId))
        {
            return;
        }

        var task = new BackgroundTaskViewModel(toolUseId, toolName, input);
        _backgroundTaskById[toolUseId] = task;
        BackgroundTasks.Insert(0, task);
        OnPropertyChanged(nameof(HasBackgroundTasks));

        // Durations tick once a second, only while one runs.
        _backgroundTaskTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickBackgroundTasks());
        _backgroundTaskTimer.Start();
    }

    private void TickBackgroundTasks()
    {
        var running = false;
        foreach (var task in BackgroundTasks.Where(t => t.IsRunning))
        {
            task.Tick();
            running = true;
        }

        if (!running)
        {
            _backgroundTaskTimer?.Stop();
        }
    }

    /// <summary>Tasks whose end the transcript already noted: a CLI may report one both as a system line and a prompt.</summary>
    private readonly HashSet<string> _notedTasks = new(StringComparer.Ordinal);

    /// <summary>
    /// A background task ended: the transcript notes it, and a background subagent (by its Agent call's id) or a
    /// background command (by its call's id, or the task id its call returned) shows it ended.
    /// </summary>
    private void FinishTask(string? toolUseId, string? taskId, string? status, string summary)
    {
        if (_notedTasks.Add(taskId ?? toolUseId ?? summary))
        {
            Items.Add(new NoticeItem(summary));
        }

        if (toolUseId is not null && _subagentById.TryGetValue(toolUseId, out var agent))
        {
            agent.Finish(failed: status == "failed", result: null);
            return;
        }

        var task = toolUseId is not null && _backgroundTaskById.TryGetValue(toolUseId, out var byCall) ? byCall
            : taskId is not null ? BackgroundTasks.FirstOrDefault(t => t.TaskId == taskId) : null;
        task?.Finish(failed: status is "failed" or "killed", summary);
    }

    /// <summary>The process went, and its background commands with it.</summary>
    private void FinishBackgroundTasks()
    {
        foreach (var task in BackgroundTasks.Where(t => t.IsRunning))
        {
            task.Finish(failed: false, "Stopped with Claude Code");
        }
    }

    // ------------------------------------------------------------------ side panel

    /// <summary>The folder's project section in the side panel (null without a folder).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    public partial ProjectViewModel? Project { get; set; }

    public bool HasProject => Project is not null;

    /// <summary>The MCP servers: configured ones until the CLI starts, then as it reports them.</summary>
    public ObservableCollection<ConnectorRow> Connectors { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoConnectors))]
    public partial bool HasConnectors { get; set; }

    private bool _connectorsFromCli;

    public string ContextPercentText => ContextTokens > 0 ? $"{ContextFraction:P0} used" : "Empty";

    public string ContextAmountText => $"{Format.Tokens(ContextTokens)} / {Format.Tokens(Catalog.ContextWindow(Model))}";

    /// <summary>Reloads what the side panel shows: the project's git state and the configured MCP servers.</summary>
    public void RefreshPanel(bool refresh = false)
    {
        Project = Folder is { } folder ? _main.ProjectFor(folder) : null;
        if (Project is { } project)
        {
            _ = project.LoadAsync(refresh);
        }

        if (!_connectorsFromCli)
        {
            _ = LoadConfiguredConnectorsAsync();
        }
    }

    private async Task LoadConfiguredConnectorsAsync()
    {
        var folder = Folder;
        IsLoadingConnectors = Connectors.Count == 0;
        var servers = await McpServers.ConfiguredAsync(folder);
        if (!_connectorsFromCli && folder == Folder)
        {
            SetConnectors(servers);
        }
    }

    /// <summary>The MCP card has nothing to show yet: its placeholder rows stand in for "None configured".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoConnectors))]
    public partial bool IsLoadingConnectors { get; set; }

    public bool ShowNoConnectors => !HasConnectors && !IsLoadingConnectors;

    private void SetConnectors(IEnumerable<McpServerInfo> servers)
    {
        Connectors.Clear();
        foreach (var s in servers)
        {
            Connectors.Add(new ConnectorRow(s));
        }

        HasConnectors = Connectors.Count > 0;
        IsLoadingConnectors = false;
    }

    // ------------------------------------------------------------------ composer

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComposerText))]
    [NotifyPropertyChangedFor(nameof(ShowSendButton))]
    [NotifyPropertyChangedFor(nameof(SendToolTip))]
    public partial string ComposerText { get; set; } = "";

    public bool HasComposerText => !string.IsNullOrWhiteSpace(ComposerText);

    /// <summary>Something to send: while a turn runs, sending queues it.</summary>
    public bool ShowSendButton => HasComposerText || HasAttachments;

    public string SendToolTip => IsTurnRunning ? "Queue (Enter): sent when Claude is done · Ctrl+Enter or Ctrl+click sends now" : "Send (Enter)";

    /// <summary>
    /// Ctrl+Enter (or Ctrl+click on Send) while Claude works: the prompt goes in the queue and the queue is sent at once
    /// (Claude is stopped), as "Send now" does. False when there is nothing to send, or no turn running (then it is a plain send).
    /// </summary>
    public bool SendNowFromComposer()
    {
        if (!IsTurnRunning)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ComposerText) || Attachments.Count > 0)
        {
            if (!SendCommand.CanExecute(null))
            {
                return false;
            }

            SendCommand.Execute(null);
        }

        if (Queued.Count == 0)
        {
            return false;
        }

        SendQueuedNowCommand.Execute(null);
        return true;
    }

    partial void OnComposerTextChanged(string value)
    {
        UpdateCommandMenu();
        // Typing starts a prompt of your own; erased again, the suggestion comes back.
        if (value.Length > 0)
        {
            _shelvedSuggestion ??= Suggestion;
            Suggestion = null;
        }
        else if (_shelvedSuggestion is { } shelved)
        {
            _shelvedSuggestion = null;
            Suggestion = shelved;
        }
    }

    /// <summary>The suggestion while something is typed over it.</summary>
    private string? _shelvedSuggestion;

    /// <summary>The CLI's guess at the next prompt: ghost text in the empty composer, taken with Tab, → or a right-click.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerPlaceholder))]
    [NotifyPropertyChangedFor(nameof(HasSuggestion))]
    public partial string? Suggestion { get; set; }

    public bool HasSuggestion => !string.IsNullOrEmpty(Suggestion);

    /// <summary>Puts the suggestion in the composer to edit or send. False when there is none.</summary>
    public bool AcceptSuggestion()
    {
        if (Suggestion is not { Length: > 0 } text || ComposerText.Length > 0)
        {
            return false;
        }

        Suggestion = null;
        ComposerText = text;
        return true;
    }

    /// <summary>Thinking and tool lines are shown (the "Show activity" setting).</summary>
    public bool ShowActivity => _main.ShowActivity;

    public void OnShowActivityChanged() => OnPropertyChanged(nameof(ShowActivity));

    /// <summary>The usage ring is shown in the status row (a setting).</summary>
    public bool ShowUsageRing => _main.ShowUsageRing;

    public void OnShowUsageRingChanged() => OnPropertyChanged(nameof(ShowUsageRing));

    [RelayCommand]
    private void ToggleActivity() => _main.ShowActivity = !_main.ShowActivity;

    // ------------------------------------------------------------------ attachments

    /// <summary>Images and files pasted, dropped or picked into the composer, sent with the next prompt.</summary>
    public ObservableCollection<Attachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public void AddAttachments(IEnumerable<Attachment> attachments)
    {
        foreach (var attachment in attachments)
        {
            // The same file twice would only repeat its path.
            if (attachment.Path is null || !Attachments.Any(a => string.Equals(a.Path, attachment.Path, StringComparison.OrdinalIgnoreCase)))
            {
                Attachments.Add(attachment);
            }
        }

        OnAttachmentsChanged();
    }

    [RelayCommand]
    private void RemoveAttachment(Attachment image)
    {
        Attachments.Remove(image);
        OnAttachmentsChanged();
    }

    private void OnAttachmentsChanged()
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(ShowSendButton));
    }

    // ------------------------------------------------------------------ queue

    /// <summary>Prompts written while a turn ran: sent together when it ends, or at once with "Send now".</summary>
    public ObservableCollection<QueuedPrompt> Queued { get; } = [];

    public bool HasQueued => Queued.Count > 0;

    // "Send now" stopped the turn: the queue goes out when it has ended.
    private bool _sendQueuedOnStop;

    // The turn ended while a queued prompt was being edited: the queue goes out when the edit is done.
    private bool _sendAfterEdit;

    public void Queue(string text, IReadOnlyList<Attachment> attachments)
    {
        Queued.Add(new QueuedPrompt(text, attachments));
        OnPropertyChanged(nameof(HasQueued));
    }

    [RelayCommand]
    private void RemoveQueued(QueuedPrompt prompt)
    {
        Queued.Remove(prompt);
        OnPropertyChanged(nameof(HasQueued));
        SendIfEditsDone();
    }

    /// <summary>Opens a queued prompt for editing (any other one being edited is saved first).</summary>
    public void EditQueued(QueuedPrompt prompt)
    {
        foreach (var other in Queued.Where(q => q.IsEditing && !ReferenceEquals(q, prompt)).ToList())
        {
            EndQueuedEdit(other, save: true);
        }

        prompt.EditText = prompt.Text;
        prompt.IsEditing = true;
    }

    /// <summary>Saves or drops an edit. Saved empty (and with nothing attached), the prompt leaves the queue.</summary>
    public void EndQueuedEdit(QueuedPrompt prompt, bool save)
    {
        if (!prompt.IsEditing)
        {
            return;
        }

        prompt.IsEditing = false;
        if (save)
        {
            var text = prompt.EditText.Trim();
            if (text.Length == 0 && prompt.Attachments.Count == 0)
            {
                Queued.Remove(prompt);
                OnPropertyChanged(nameof(HasQueued));
            }
            else
            {
                prompt.Text = text;
            }
        }

        SendIfEditsDone();
    }

    private void SendIfEditsDone()
    {
        if (_sendAfterEdit && !Queued.Any(q => q.IsEditing))
        {
            _sendAfterEdit = false;
            _ = SendQueuedAsync();
        }
    }

    /// <summary>Stops the running turn and sends the queue right away.</summary>
    [RelayCommand]
    private async Task SendQueuedNowAsync()
    {
        foreach (var editing in Queued.Where(q => q.IsEditing).ToList())
        {
            EndQueuedEdit(editing, save: true);
        }

        if (IsTurnRunning)
        {
            _sendQueuedOnStop = true;
            await StopAsync();
        }
        else
        {
            await SendQueuedAsync();
        }
    }

    /// <summary>Everything queued, as one prompt (as Claude Code merges queued messages).</summary>
    private async Task SendQueuedAsync()
    {
        _sendQueuedOnStop = false;
        if (Queued.Count == 0 || IsTurnRunning || IsPhoneSession)
        {
            return;
        }

        // Not half-way through an edit: it goes out once that is saved or dropped.
        if (Queued.Any(q => q.IsEditing))
        {
            _sendAfterEdit = true;
            return;
        }

        var text = string.Join("\n\n", Queued.Select(q => q.Text).Where(t => t.Length > 0));
        var attachments = Queued.SelectMany(q => q.Attachments).ToList();
        Queued.Clear();
        OnPropertyChanged(nameof(HasQueued));
        await SendPromptAsync(text, attachments);
    }

    // ------------------------------------------------------------------ slash commands

    /// <summary>The "/" menu: Claude Code's commands matching what is typed after the slash.</summary>
    public ObservableCollection<CommandOption> CommandOptions { get; } = [];

    [ObservableProperty]
    public partial bool IsCommandMenuOpen { get; set; }

    // Esc closed the menu: it stays closed until the slash is typed again.
    private bool _commandMenuDismissed;

    private void UpdateCommandMenu()
    {
        var text = ComposerText;
        if (!text.StartsWith('/') || text.Any(char.IsWhiteSpace) || IsPhoneSession)
        {
            _commandMenuDismissed = false;
            IsCommandMenuOpen = false;
            return;
        }

        if (_commandMenuDismissed)
        {
            return;
        }

        var query = text[1..];
        var matches = _main.SlashCommands
            .Select(c => (Command: c, Rank: c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2))
            .Where(m => m.Rank < 2)
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Command.Name.Contains(':'))
            .ThenBy(m => m.Command.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.Command)
            .ToList();

        CommandOptions.Clear();
        foreach (var command in matches)
        {
            CommandOptions.Add(new CommandOption(command));
        }

        if (CommandOptions.Count > 0)
        {
            CommandOptions[0].IsSelected = true;
        }

        IsCommandMenuOpen = CommandOptions.Count > 0;
    }

    public void MoveCommandSelection(int delta)
    {
        if (CommandOptions.Count == 0)
        {
            return;
        }

        var index = CommandOptions.ToList().FindIndex(o => o.IsSelected);
        var next = ((index < 0 ? 0 : index + delta) % CommandOptions.Count + CommandOptions.Count) % CommandOptions.Count;
        for (var i = 0; i < CommandOptions.Count; i++)
        {
            CommandOptions[i].IsSelected = i == next;
        }
    }

    public CommandOption? SelectedCommand => CommandOptions.FirstOrDefault(o => o.IsSelected);

    /// <summary>Puts <c>/name </c> in the composer; true when the command takes no arguments, so it can be sent as is.</summary>
    public bool AcceptCommand(CommandOption option)
    {
        IsCommandMenuOpen = false;
        _commandMenuDismissed = true;
        ComposerText = "/" + option.Name + " ";
        return !option.HasArgumentHint;
    }

    public void DismissCommandMenu()
    {
        _commandMenuDismissed = true;
        IsCommandMenuOpen = false;
    }

    // While Claude works: Enter queues the prompt, Ctrl+Enter stops Claude and sends it at once.
    public string ComposerPlaceholder => HasSuggestion ? Suggestion! : IsNew ? "Describe a task or ask a question"
        : IsTurnRunning ? "Enter queues a message · Ctrl+Enter sends it now" : "Type / for commands";

    // ------------------------------------------------------------------ turn state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendToolTip))]
    [NotifyPropertyChangedFor(nameof(ComposerPlaceholder))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsTurnRunning { get; set; }

    public bool IsIdle => !IsTurnRunning;

    // The working row at the bottom of the transcript: what Claude is doing, and for how long.
    private DispatcherTimer? _workingTimer;
    private DateTime _turnStartUtc;

    /// <summary>"Thinking", the running tool group's summary ("Running a command"), or "Working".</summary>
    [ObservableProperty]
    public partial string WorkingText { get; set; } = "Working";

    /// <summary>"8s", "13m 11s".</summary>
    [ObservableProperty]
    public partial string WorkingElapsed { get; set; } = "";

    partial void OnIsTurnRunningChanged(bool value)
    {
        if (value)
        {
            _turnStartUtc = DateTime.UtcNow;
            Suggestion = _shelvedSuggestion = null; // It was a guess at this prompt.
            _workingTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateWorking());
            _workingTimer.Start();
            UpdateWorking();
        }
        else
        {
            _workingTimer?.Stop();
            _echoedGroup?.IsEchoed = false;
            _echoedGroup = null;
        }

        _main.OnTurnRunningChanged(this);
    }

    partial void OnSessionIdChanged(string? value) => _main.OnTurnRunningChanged(this);

    private void UpdateWorking()
    {
        var elapsed = DateTime.UtcNow - _turnStartUtc;
        WorkingElapsed = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
        WorkingText = Items.LastOrDefault() switch
        {
            ThinkingItem { IsStreaming: true } or ToolGroupItem { IsThinking: true } => "Thinking",
            ToolGroupItem { IsRunning: true } group when group.Calls.LastOrDefault(c => c.IsRunning) is { } running => ToolText.RunningAction(running.Name, running.Input),
            AssistantTextItem { IsStreaming: true } => "Writing",
            PermissionItem { IsAnswered: false } => "Waiting for you",
            _ => "Working",
        };

        // The running group above saying the same thing fades out (its place kept).
        var echoed = IsTurnRunning && Items.LastOrDefault() is ToolGroupItem { IsRunning: true } last && SameStatus(last.Summary, WorkingText) ? last : null;
        if (!ReferenceEquals(echoed, _echoedGroup))
        {
            _echoedGroup?.IsEchoed = false;
            _echoedGroup = echoed;
        }

        echoed?.IsEchoed = true;

        // As the desktop app: waiting on you, thinking (also before anything has come back), the running tool's kind.
        WorkingActivity = PendingPermission is not null ? "blocked" : Items.LastOrDefault() switch
        {
            ThinkingItem { IsStreaming: true } or ToolGroupItem { IsThinking: true } or UserMessageItem => "think",
            ToolGroupItem { IsRunning: true } group when group.Calls.LastOrDefault(c => c.IsRunning) is { } call => ToolText.MarkActivity(call.Name, call.Input),
            _ => "default",
        };
    }

    private ToolGroupItem? _echoedGroup;

    private static bool SameStatus(string a, string b) =>
        string.Equals(a.TrimEnd('…', '.', ' '), b.TrimEnd('…', '.', ' '), StringComparison.OrdinalIgnoreCase);

    /// <summary>The working mark's animation: "think", "read", "search", "code", "write", "compose", "blocked", "default".</summary>
    [ObservableProperty]
    public partial string WorkingActivity { get; set; } = "think";

    /// <summary>The agent status line is shown once a turn ran in this app session.</summary>
    [ObservableProperty]
    public partial bool HasRun { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsStateRunning))]
    [NotifyPropertyChangedFor(nameof(IsStateDone))]
    [NotifyPropertyChangedFor(nameof(IsStateError))]
    [NotifyPropertyChangedFor(nameof(IsStateInterrupted))]
    [NotifyPropertyChangedFor(nameof(AgentMetaLine))]
    public partial AgentState State { get; set; }

    public string StateText => State switch
    {
        AgentState.Running => "Running",
        AgentState.Done => "Done",
        AgentState.Error => "Error",
        AgentState.Interrupted => "Interrupted",
        _ => "",
    };

    public bool IsStateRunning => State == AgentState.Running;

    public bool IsStateDone => State == AgentState.Done;

    public bool IsStateError => State == AgentState.Error;

    public bool IsStateInterrupted => State == AgentState.Interrupted;

    /// <summary>"Opus 5.5 · last turn 1:08 · 2.9k tokens · 13 tools".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentMetaLine))]
    public partial string AgentMeta { get; set; } = "";

    /// <summary>The muted part of the agent status line, after the state word.</summary>
    public string AgentMetaLine => State == AgentState.Running || AgentMeta.Length == 0
        ? "· " + ModelName
        : "· " + AgentMeta;

    /// <summary>Share of the context window used after the last turn (0..1), for the ring.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextToolTip))]
    [NotifyPropertyChangedFor(nameof(ContextPercentText))]
    public partial double ContextFraction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextToolTip))]
    [NotifyPropertyChangedFor(nameof(ContextPercentText))]
    [NotifyPropertyChangedFor(nameof(ContextAmountText))]
    public partial long ContextTokens { get; set; }

    public string ContextToolTip => ContextTokens > 0
        ? $"Context: {Format.Tokens(ContextTokens)} of {Format.Tokens(Catalog.ContextWindow(Model))} ({ContextFraction:P0})"
        : "Context: empty";

    [ObservableProperty]
    public partial bool IsLoadingHistory { get; set; }

    [ObservableProperty]
    public partial PermissionItem? PendingPermission { get; set; }

    partial void OnPendingPermissionChanged(PermissionItem? value) => _main.OnWaitingChanged(this);

    public bool IsProcessRunning => _backend?.IsProcessRunning == true;

    /// <summary>The app's plan usage, for the ring's popup.</summary>
    public UsageViewModel Usage => _main.Usage;

    /// <summary>When the CLI process last started or sent anything; drives the idle timeout.</summary>
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>A subagent or background command is still at work, so the idle timeout leaves the process alone.</summary>
    public bool HasBackgroundWork => BackgroundTasks.Any(t => t.IsRunning) || Subagents.Any(a => a.IsRunning);

    /// <summary>Raised after items were added or grew, so the view can follow the bottom.</summary>
    public event Action? ContentChanged;

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    public async Task SendAsync()
    {
        var text = ComposerText.Trim();
        if ((text.Length == 0 && Attachments.Count == 0) || IsPhoneSession)
        {
            return;
        }

        // "/model sonnet" switches like the model chips (asking first in a conversation), not behind their back.
        if (Attachments.Count == 0 && ModelFromCommand(text) is { } model)
        {
            ComposerText = "";
            await SelectModelAsync(model);
            return;
        }

        // Two processes on one transcript would interleave it: the window or terminal running it keeps it.
        if (!IsProcessRunning && SessionId is { } id && _main.IsRunningElsewhere(id))
        {
            Items.Add(new ErrorItem("This session is running in another Nomad window or a terminal. End it there first, or fork it here."));
            ContentChanged?.Invoke();
            return;
        }

        var images = Attachments.ToList();
        ComposerText = "";
        Attachments.Clear();
        OnAttachmentsChanged();

        // While Claude works, a prompt waits its turn (above the composer, with "Send now").
        if (IsTurnRunning)
        {
            Queue(text, images);
            return;
        }

        await SendPromptAsync(text, images);
    }

    public async Task SendPromptAsync(string text, IReadOnlyList<Attachment>? attachments = null)
    {
        attachments ??= [];
        var images = attachments.Where(a => a.IsImage).ToList();
        var files = attachments.Where(a => a.IsFile).ToList();
        var first = IsNew;
        var firstTitle = SessionTitles.Shorten(text.Length > 0 ? text : files.Count > 0 ? files[0].Name : "Image");
        if (first)
        {
            Title = text.Length > 0 ? UntitledTitle : firstTitle;
            _worktreePending = UseWorktree && IsGitFolder && Folder is not null;
            IsNew = false;
        }

        CloseGroup();
        var message = new UserMessageItem(text, Guid.NewGuid().ToString(), DateTime.UtcNow, seen: false)
        {
            Images = images.Select(i => i.Preview!).ToList(),
            Files = files,
        };
        text = AttachedFiles.AddTo(text, files.Select(f => f.Path!));
        _unseen = message;
        Items.Add(message);
        IsTurnRunning = true;
        State = AgentState.Running;
        HasRun = true;
        ContentChanged?.Invoke();
        _main.OnSessionActivity(this);

        try
        {
            // Worktree on: the session works in a new worktree of the repository (tried again with the next prompt if git failed).
            if (_worktreePending && Folder is { } repo)
            {
                Folder = await GitInfo.AddWorktreeAsync(repo, WorktreeFrom);
                _worktreePending = false;
            }

            await EnsureBackendAsync();
            await _backend!.SendPromptAsync(text, message.Uuid, images.Select(i => i.Image!).ToList());

            // The process now holds the rewind / fork point and the transcript will record it.
            _resumeAt = null;
            _forkFrom = null;
            if (first && Title == UntitledTitle)
            {
                _ = GenerateTitleAsync(_backend, text, firstTitle);
            }

            // A new session gets its bridge once the prompt gave it a name (the generated one, if it waits for that).
            _ = SyncRemoteControlAsync();
        }
        catch (Exception ex)
        {
            if (Title == UntitledTitle)
            {
                Title = firstTitle; // No CLI to name it.
            }

            IsTurnRunning = false;
            State = AgentState.Error;
            Items.Add(new ErrorItem(ex.Message));
            ContentChanged?.Invoke();
            _main.OnSessionActivity(this);
        }
    }

    /// <summary>
    /// As the desktop app does: once, from the first prompt (trimmed, at most 2000 characters), the CLI generates a short
    /// title (stored with the session as its ai-title). "Untitled session" shows meanwhile; the shortened prompt if it
    /// fails. A rename in the meantime wins, and with CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC set nothing is asked.
    /// </summary>
    private async Task GenerateTitleAsync(IAgentBackend backend, string prompt, string fallback)
    {
        string? title = null;
        try
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC")))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                title = SessionTitles.Clean(await backend.GenerateTitleAsync(SessionTitles.Description(prompt), persist: true, timeout.Token));
            }
        }
        catch (Exception)
        {
            // The shortened prompt stands in.
        }

        if (Title == UntitledTitle && RenamedTitle is null)
        {
            Title = title ?? fallback;
            _main.OnSessionTitleChanged(this);
        }

        // The bridge waited for the name, as claude.ai keeps the first one it gets.
        await SyncRemoteControlAsync();
    }

    /// <summary>A new session's title until its generated one arrives.</summary>
    public const string UntitledTitle = "Untitled session";

    [RelayCommand]
    public async Task StopAsync()
    {
        if (_backend is not null && IsTurnRunning)
        {
            await _backend.InterruptAsync();
        }
    }

    [RelayCommand]
    public async Task SelectModelAsync(string id)
    {
        if (Model == id)
        {
            PendingModel = null;
            return;
        }

        // A conversation with history is cached for its model: switching rereads all of it, so ask first.
        if (_main.Settings.ConfirmModelSwitch && Items.OfType<UserMessageItem>().Any())
        {
            PendingModel = id;
            return;
        }

        await SwitchModelAsync(id);
    }

    /// <summary>A model switch waiting for the card above the composer to confirm it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingModel), nameof(PendingModelTitle), nameof(PendingModelText))]
    public partial string? PendingModel { get; set; }

    public bool HasPendingModel => PendingModel is not null;

    public string PendingModelTitle => $"Switch this conversation to {Catalog.ModelName(PendingModel)}?";

    public string PendingModelText =>
        $"The conversation is cached for {Catalog.ModelName(Model)}. {Catalog.ModelName(PendingModel)}'s first reply reads all of it again, " +
        "which is slower and uses more of your plan.";

    [RelayCommand]
    private async Task ConfirmModelSwitchAsync(string? how)
    {
        if (PendingModel is not { } id)
        {
            return;
        }

        PendingModel = null;
        if (how == "always")
        {
            _main.Settings.ConfirmModelSwitch = false;
            _main.SaveSettings();
        }

        await SwitchModelAsync(id);
    }

    [RelayCommand]
    private void CancelModelSwitch() => PendingModel = null;

    /// <summary>The model "/model &lt;id or name&gt;" names ("sonnet", "Opus 5.5", "claude-haiku-5-5"), or null.</summary>
    private static string? ModelFromCommand(string text)
    {
        if (!text.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var arg = text[7..].Trim();
        return Catalog.Models.FirstOrDefault(m =>
            string.Equals(m.Id, arg, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.DisplayName, arg, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.DisplayName.Split(' ')[0], arg, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private async Task SwitchModelAsync(string id)
    {
        Model = id;
        if (_backend is { IsProcessRunning: true } backend && !await backend.SetModelAsync(id))
        {
            _restartRequired = true;
        }

        // Each model comes back at the effort last chosen with it.
        await ApplyEffortAsync(_main.EffortFor(id));
    }

    /// <summary>A mode was picked on this session (so the default no longer applies to it).</summary>
    private bool _modePicked;

    /// <summary>
    /// The new-session page is kept between visits: until a mode is picked on it, it follows the default mode, also
    /// when that changes in the settings meanwhile.
    /// </summary>
    public void ApplyDefaultMode()
    {
        if (IsNew && !_modePicked)
        {
            PermissionMode = _main.Settings.DefaultPermissionMode;
        }
    }

    [RelayCommand]
    public async Task SelectModeAsync(string id)
    {
        if (PermissionMode == id)
        {
            return;
        }

        PermissionMode = id;
        _modePicked = true;
        if (_backend is { IsProcessRunning: true } backend)
        {
            _suppressModeSync = true;
            try
            {
                if (!await backend.SetPermissionModeAsync(id))
                {
                    // e.g. bypassPermissions needs a process launched with it: restart before the next prompt.
                    _restartRequired = true;
                }
            }
            finally
            {
                _suppressModeSync = false;
            }
        }
    }

    [RelayCommand]
    public async Task SelectEffortAsync(string id)
    {
        _main.RememberEffort(Model, id);
        await ApplyEffortAsync(id);
    }

    private async Task ApplyEffortAsync(string id)
    {
        if (Effort == id)
        {
            return;
        }

        Effort = id;
        if (_backend is { IsProcessRunning: true } backend && (string.IsNullOrEmpty(id) || !await backend.SetEffortAsync(id)))
        {
            _restartRequired = true;
        }
    }

    [RelayCommand]
    public async Task ToggleFastModeAsync()
    {
        FastMode = !FastMode;
        if (_backend is { IsProcessRunning: true } backend && !await backend.SetFastModeAsync(FastMode))
        {
            _restartRequired = true;
        }
    }

    /// <summary>
    /// Remote Control on or off. Turning it on for a stored session without a process starts one
    /// (resuming the session), since only a running process can be reached from the phone.
    /// </summary>
    [RelayCommand]
    public async Task ToggleRemoteControlAsync()
    {
        IsRemoteControl = !IsRemoteControl;
        _remoteFailedBackend = null;
        if (IsRemoteControl && !IsNew && SessionId is not null && _backend is not { IsProcessRunning: true })
        {
            await WakeAsync();
            return;
        }

        await SyncRemoteControlAsync();
    }

    private IAgentBackend? _remoteOnBackend;
    private IAgentBackend? _remoteFailedBackend;
    private string? _bridgeSessionId;
    private bool _remoteSyncing;

    /// <summary>Brings the running process's bridge in line with <see cref="IsRemoteControl"/>.</summary>
    private async Task SyncRemoteControlAsync()
    {
        if (_remoteSyncing || _backend is not { IsProcessRunning: true } backend || ReferenceEquals(backend, _remoteFailedBackend))
        {
            return;
        }

        var on = IsRemoteControl;
        if (on == ReferenceEquals(_remoteOnBackend, backend) || (on && Title == UntitledTitle))
        {
            return;
        }

        _remoteSyncing = true;
        try
        {
            if (on)
            {
                var name = string.IsNullOrWhiteSpace(Title) ? "Nomad session" : Title;
                var reattach = SessionId is { } id && _main.Settings.RemoteControlBridges.TryGetValue(id, out var bridge) ? bridge : null;
                var info = await backend.SetRemoteControlAsync(true, name, reattach);
                if (info?.SessionUrl is null && reattach is not null && ReferenceEquals(backend, _backend))
                {
                    // The old claude.ai session is gone: start a fresh one.
                    info = await backend.SetRemoteControlAsync(true, name);
                }

                if (!ReferenceEquals(backend, _backend))
                {
                    return;
                }

                if (info?.SessionUrl is { } url)
                {
                    _remoteOnBackend = backend;
                    RemoteControlUrl = url;
                    _bridgeSessionId = info.BridgeSessionId;
                    RememberBridge();
                }
                else
                {
                    _remoteFailedBackend = backend;
                    RemoteControlState = "failed";
                }
            }
            else
            {
                await backend.SetRemoteControlAsync(false, null);
                _remoteOnBackend = null;
                RemoteControlUrl = null;
                RemoteControlState = "";
                if (SessionId is { } id && _main.Settings.RemoteControlBridges.Remove(id))
                {
                    _main.SaveSettings();
                }
            }
        }
        finally
        {
            _remoteSyncing = false;
        }

        // The switch may have flipped again while the request ran.
        if (IsRemoteControl != ReferenceEquals(_remoteOnBackend, _backend))
        {
            await SyncRemoteControlAsync();
        }
    }

    private void RememberBridge()
    {
        if (SessionId is { } id && _bridgeSessionId is { } bridge && IsRemoteControl)
        {
            _main.Settings.RememberBridge(id, bridge);
            _main.SaveSettings();
        }
    }

    private void ForgetBridgeProcess()
    {
        _remoteOnBackend = null;
        _remoteFailedBackend = null;
        RemoteControlUrl = null;
        RemoteControlState = "";
    }

    /// <summary>The first prompt went out with Worktree on and the worktree is still to be made.</summary>
    private bool _worktreePending;

    public void SetFolder(string? folder)
    {
        if (!IsNew)
        {
            return; // A running conversation keeps its folder.
        }

        // Remembered as a recent folder once a prompt is sent (MainViewModel.OnSessionActivity).
        Folder = string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    /// <summary>
    /// Enter / Esc on a pending permission or question card. <paramref name="pickDefaults"/> (dev hook)
    /// answers a question with its first options instead of waiting for a choice.
    /// </summary>
    public void AnswerPending(PermissionChoice choice, bool pickDefaults = false)
    {
        if (PendingPermission is { IsAnswered: false } pending)
        {
            if (pickDefaults && pending is QuestionItem question && choice != PermissionChoice.Deny)
            {
                question.PickDefaults();
            }

            Answer(pending, choice);
        }
    }

    /// <summary>Expands every tool group (dev hook for screenshots).</summary>
    public void ExpandAllTools()
    {
        foreach (var group in Items.OfType<ToolGroupItem>())
        {
            group.IsExpanded = true;
        }

        foreach (var thinking in Items.OfType<ThinkingItem>().Concat(Items.OfType<ToolGroupItem>().SelectMany(g => g.Entries.OfType<ThinkingItem>())))
        {
            thinking.IsExpanded = true;
        }
    }

    // ------------------------------------------------------------------ history

    /// <summary>Fills the transcript from a stored session (no process is started until the next prompt).</summary>
    public void LoadHistory(SessionSummary summary, SessionHistory history)
    {
        SessionId = history.SessionId;
        Title = summary.Title;
        IsNew = false;
        Folder = history.WorkingDirectory ?? summary.WorkingDirectory;
        if (!string.IsNullOrEmpty(history.Model))
        {
            Model = history.Model;
        }

        Items.Clear();
        _historyCount = 0;
        AppendHistory(history, summary.LastActivityUtc);
    }

    private static Avalonia.Media.Imaging.Bitmap? DecodeImage(HistoryImage image)
    {
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(image.Base64));
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception)
        {
            return null; // Corrupt or a type Skia can't read.
        }
    }

    /// <summary>Adds the history items not shown yet (all of them on the first load).</summary>
    private void AppendHistory(SessionHistory history, DateTime fallbackTimeUtc)
    {
        if (history.Items.Count <= _historyCount)
        {
            return;
        }

        foreach (var item in history.Items.Skip(_historyCount))
        {
            switch (item)
            {
                case HistoryUserMessage u:
                    CloseGroup();
                    var (promptText, promptFiles) = AttachedFiles.Split(u.Text);
                    Items.Add(new UserMessageItem(promptText, u.Uuid, u.TimestampUtc ?? fallbackTimeUtc, seen: true)
                    {
                        ParentUuid = u.ParentUuid,
                        Files = promptFiles,
                        Images = u.Images.Select(DecodeImage).OfType<Avalonia.Media.Imaging.Bitmap>().ToList(),
                    });
                    break;
                case HistoryAssistantText a:
                    CloseGroup();
                    Items.Add(new AssistantTextItem(a.MessageId, -1, a.Text, streaming: false));
                    break;
                case HistoryThinking t:
                    AddThinking(new ThinkingItem("", -1, t.Text, streaming: false));
                    break;
                case HistoryToolCall c:
                    var call = AddCall(c.ToolUseId, c.Name);
                    call.SetInput(c.Input);
                    break;
                case HistoryToolResult r:
                    CompleteCall(r.ToolUseId, r.IsError, r.Text, r.Structured);
                    break;
                case HistoryNotice n:
                    CloseGroup();
                    Items.Add(new NoticeItem(n.Text));
                    break;
            }
        }

        _historyCount = history.Items.Count;

        // While the phone runs it, the last tool calls may still be working.
        if (!IsPhoneSession)
        {
            FinishOpenCalls();
        }

        CloseGroup();
        ContentChanged?.Invoke();
    }

    // ------------------------------------------------------------------ phone sessions

    private int _historyCount;
    private string? _phoneFile;
    private long _phoneFileLength;
    private DispatcherTimer? _phoneTimer;
    private bool _phoneReading;

    /// <summary>
    /// Started from the phone and running there: Nomad shows the transcript as it grows, and the
    /// composer waits until the phone's process ends (then the next prompt resumes it here).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowComposer))]
    public partial bool IsPhoneSession { get; set; }

    public bool ShowComposer => !IsPhoneSession;

    /// <summary>
    /// Ends the phone's process and continues here. Remote Control reattaches to the same claude.ai
    /// session, so the app keeps showing it and can still send prompts (now to Nomad's process).
    /// </summary>
    [RelayCommand]
    private async Task TakeOverAsync()
    {
        if (!IsPhoneSession || _phone is not { } phone)
        {
            return;
        }

        if (SessionId is { } id)
        {
            _main.Settings.RememberBridge(id, phone.RemoteId);
            _main.SaveSettings();
        }

        IsRemoteControl = true;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(phone.ProcessId);
            process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // Already gone.
        }

        _main.OnPhoneSessionEnded(this);
        SetPhoneSession(null, null);
        await WakeAsync();
    }

    private Nomad.Core.Remote.PhoneSessionProcess? _phone;

    public void SetPhoneSession(Nomad.Core.Remote.PhoneSessionProcess? phone, string? file)
    {
        _phone = phone;
        var running = phone is not null;
        if (running && file is not null)
        {
            _phoneFile = file;
            IsPhoneSession = true;
            _phoneTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = ReadPhoneTranscriptAsync());
            _phoneTimer.Start();
        }
        else if (IsPhoneSession)
        {
            IsPhoneSession = false;
            _phoneTimer?.Stop();
            _ = ReadPhoneTranscriptAsync(final: true);
        }
    }

    private async Task ReadPhoneTranscriptAsync(bool final = false)
    {
        if (_phoneReading || _phoneFile is not { } file)
        {
            return;
        }

        long length;
        try
        {
            length = new FileInfo(file).Length;
        }
        catch (IOException)
        {
            return;
        }

        if (length == _phoneFileLength && !final)
        {
            return;
        }

        _phoneReading = true;
        try
        {
            var history = await Task.Run(() => SessionStore.LoadHistory(file));
            _phoneFileLength = length;
            if (IsNew && history.Items.OfType<HistoryUserMessage>().FirstOrDefault() is { } first)
            {
                IsNew = false;
                if (string.IsNullOrEmpty(Title))
                {
                    Title = SessionTitles.Shorten(first.Text.Length > 0 ? first.Text : "Image");
                }
            }

            AppendHistory(history, DateTime.UtcNow);
            if (final)
            {
                FinishOpenCalls();
            }

            LastActivityUtc = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being written: the next tick tries again.
        }
        finally
        {
            _phoneReading = false;
        }
    }

    // ------------------------------------------------------------------ backend

    private async Task EnsureBackendAsync()
    {
        if (_backend is { IsProcessRunning: true } && !_restartRequired)
        {
            return;
        }

        if (_backend is not null)
        {
            var old = _backend;
            _backend = null;
            await old.DisposeAsync();
        }

        _restartRequired = false;
        Folder ??= _main.CreateScratchFolder();
        var backend = new ClaudeCodeBackend();
        if (_main.Dev.LogPath is { } log)
        {
            backend.RawLine += (line, outgoing) => File.AppendAllText(log, (outgoing ? "> " : "< ") + line + Environment.NewLine);
        }
        backend.EventReceived += e => Dispatcher.UIThread.Post(() => Apply(backend, e));
        await backend.StartAsync(new AgentStartOptions
        {
            WorkingDirectory = Folder,
            Model = Model,
            PermissionMode = PermissionMode,
            Effort = string.IsNullOrEmpty(Effort) ? null : Effort,
            ThinkingDisplay = "summarized",
            PromptSuggestions = _main.Settings.PromptSuggestions,
            FastMode = FastMode,
            ResumeSessionId = SessionId ?? _forkFrom,
            ResumeSessionAt = _resumeAt,
            ForkSession = SessionId is null && _forkFrom is not null,
            CliPath = _main.Settings.CliPath,
            ExtraArguments = _main.Dev.ExtraCliArguments,
        });
        _backend = backend;
        ForgetBridgeProcess();
        LastActivityUtc = DateTime.UtcNow;
        _main.OnAliveChanged(this);

        // A stored session can be reached at once; a new one waits for its first prompt (and name).
        if (SessionId is not null)
        {
            _ = SyncRemoteControlAsync();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_backend is not null)
        {
            var b = _backend;
            _backend = null;
            ForgetBridgeProcess();
            await b.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ rewind and fork

    /// <summary>
    /// Rewind to just before <paramref name="message"/>: the transcript from it on goes, its text goes
    /// back into the composer, and the next prompt continues the session from the record before it
    /// (<c>--resume-session-at</c>). Files Claude changed are left as they are.
    /// </summary>
    public async Task RewindAsync(UserMessageItem message)
    {
        var index = Items.IndexOf(message);
        if (IsTurnRunning || index < 0 || message.Uuid is null)
        {
            return;
        }

        var parent = message.ParentUuid ?? ParentInTranscript(message.Uuid);
        await ShutdownAsync();
        _main.OnAliveChanged(this);

        if (parent is null)
        {
            // The first message: nothing before it to keep, so the next prompt starts a new session.
            SessionId = null;
            _resumeAt = null;
        }
        else
        {
            _resumeAt = parent;
        }

        while (Items.Count > index)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        _openGroup = null;
        _unseen = null;
        ComposerText = message.Text;
        AddAttachments(message.Files);
        ContentChanged?.Invoke();
    }

    /// <summary>Starts this (new) session as a fork of <paramref name="sessionId"/>, cut after <paramref name="resumeAt"/>.</summary>
    public void PrepareFork(string sessionId, string? resumeAt, IEnumerable<TranscriptItem> items)
    {
        _forkFrom = sessionId;
        _resumeAt = resumeAt;
        foreach (var item in items)
        {
            Items.Add(item);
        }
    }

    /// <summary>The record before <paramref name="uuid"/> in this session's transcript.</summary>
    public string? ParentInTranscript(string uuid) =>
        SessionId is { } id && _main.Store.FindTranscript(id) is { } file ? SessionStore.ParentOf(file, uuid) : null;

    /// <summary>Refreshes the "5 minutes ago" of every prompt.</summary>
    public void TickClock()
    {
        foreach (var message in Items.OfType<UserMessageItem>())
        {
            message.Tick();
        }
    }

    /// <summary>Starts the CLI process (resuming the session) without sending a prompt.</summary>
    public async Task WakeAsync()
    {
        if (IsPhoneSession)
        {
            return; // Its process belongs to the phone.
        }

        try
        {
            await EnsureBackendAsync();
        }
        catch (Exception ex)
        {
            Items.Add(new ErrorItem(ex.Message));
            ContentChanged?.Invoke();
        }
    }

    /// <summary>Ends the CLI process to free its memory; the next prompt resumes the session.</summary>
    public async Task SleepAsync()
    {
        if (IsTurnRunning)
        {
            return;
        }

        await ShutdownAsync();
        _main.OnAliveChanged(this);
    }

    private void Apply(IAgentBackend source, AgentEvent e)
    {
        if (!ReferenceEquals(source, _backend))
        {
            return; // A replaced process still draining.
        }

        LastActivityUtc = DateTime.UtcNow;

        // Anything back from Claude Code after a prompt means it picked the prompt up.
        if (_unseen is { } sent)
        {
            sent.IsSeen = true;
            _unseen = null;
        }

        switch (e)
        {
            case SessionStartedEvent s:
                if (!string.IsNullOrEmpty(s.SessionId) && s.SessionId != SessionId)
                {
                    SessionId = s.SessionId;
                    _main.OnSessionIdentified(this);
                    RememberBridge();
                }

                if (!string.IsNullOrEmpty(s.PermissionMode) && !_suppressModeSync)
                {
                    PermissionMode = s.PermissionMode;
                }

                if (s.McpServers is { } servers)
                {
                    _connectorsFromCli = true;
                    SetConnectors(servers.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase));
                }

                break;

            case AccountInfoEvent a:
                _main.OnAccountInfo(a);
                if (a.Commands is { Count: > 0 } commands)
                {
                    _main.OnSlashCommands(commands);
                }

                break;

            case TurnStartedEvent:
                IsTurnRunning = true;
                State = AgentState.Running;
                _turnToolCount = 0;
                break;

            case RemotePromptEvent remote:
                CloseGroup();

                // A background task's end, which the CLI hands to Claude in the user's place: a notice, not a prompt.
                if (TaskNotification.TryParse(remote.Text, out var summary))
                {
                    FinishTask(TaskNotification.ToolUseId(remote.Text), TaskNotification.TaskId(remote.Text), TaskNotification.Status(remote.Text), summary);
                }
                else
                {
                    if (IsNew)
                    {
                        Title = SessionTitles.Shorten(remote.Text);
                        IsNew = false;
                    }

                    Items.Add(new UserMessageItem(remote.Text, remote.Uuid, DateTime.UtcNow, seen: true) { IsRemote = true });
                }

                IsTurnRunning = true;
                State = AgentState.Running;
                HasRun = true;
                _turnToolCount = 0;
                ContentChanged?.Invoke();
                _main.OnSessionActivity(this);
                break;

            case PromptSuggestionEvent suggestion when !IsTurnRunning && _main.Settings.PromptSuggestions:
                if (ComposerText.Length == 0)
                {
                    Suggestion = suggestion.Text;
                }
                else
                {
                    _shelvedSuggestion = suggestion.Text; // Shown if what is typed is erased.
                }

                break;

            case CommandOutputEvent command:
                CloseGroup();
                Items.Add(new NoticeItem(command.Text));
                ContentChanged?.Invoke();
                break;

            case RemoteControlStateEvent bridge:
                RemoteControlState = bridge.State;
                break;

            case TaskNotificationEvent ended:
                CloseGroup();
                FinishTask(ended.ToolUseId, ended.TaskId, ended.Status, ended.Summary ?? "Background task finished");
                ContentChanged?.Invoke();
                break;

            case BlockStartedEvent b when b.ParentToolUseId is null:
                StartBlock(b);
                break;

            case TextDeltaEvent t when t.ParentToolUseId is null:
                if (GetOrCreateBlock(t.MessageId, t.Index, AgentBlockKind.Text) is AssistantTextItem text)
                {
                    text.Text += t.Text;
                    ContentChanged?.Invoke();
                }

                break;

            case ThinkingDeltaEvent t when t.ParentToolUseId is null:
                if (GetOrCreateBlock(t.MessageId, t.Index, AgentBlockKind.Thinking) is ThinkingItem thinking)
                {
                    thinking.Text += t.Text;
                }

                break;

            case BlockCompletedEvent c when c.ParentToolUseId is null:
                CompleteBlock(c);
                if (c.Kind == AgentBlockKind.Text && c.Text is { } said)
                {
                    _main.OnGitHubLinksMentioned(this, said);
                }

                break;

            case ToolCallEvent call when call.ParentToolUseId is null:
                var item = _calls.TryGetValue(call.ToolUseId, out var existing) ? existing : AddCall(call.ToolUseId, call.Name);
                item.SetInput(call.Input);
                _groupOfCall[call.ToolUseId].Refresh();
                if (ToolText.CategoryOf(call.Name) == ToolCategory.Agent)
                {
                    StartSubagent(call.ToolUseId, call.Input);
                }
                else if (call.Input.ValueKind == System.Text.Json.JsonValueKind.Object
                    && call.Input.TryGetProperty("run_in_background", out var bg) && bg.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    StartBackgroundTask(call.ToolUseId, call.Name, call.Input);
                }

                break;

            case ToolResultEvent r when r.ParentToolUseId is null:
                CompleteCall(r.ToolUseId, r.IsError, r.Text, r.Structured);
                _main.OnGitHubLinksMentioned(this, r.Text);

                // A background (async) subagent's call returns at once; it ends with a task notification later.
                if (_subagentById.TryGetValue(r.ToolUseId, out var finished))
                {
                    if (!r.IsError && (finished.IsBackground || r.Text.TrimStart().StartsWith("Async agent launched", StringComparison.OrdinalIgnoreCase)))
                    {
                        finished.MarkBackground();
                    }
                    else
                    {
                        finished.Finish(r.IsError, r.Text);
                    }
                }
                else if (_backgroundTaskById.TryGetValue(r.ToolUseId, out var task))
                {
                    // It failed to start, or says which task it became, for the notification that ends it.
                    if (r.IsError)
                    {
                        task.Finish(failed: true, ToolText.FirstLine(r.Text));
                    }
                    else
                    {
                        task.TaskId = TaskNotification.LaunchedTaskId(r.Text);
                    }
                }

                ContentChanged?.Invoke();
                break;

            case not null when HandleSubagentEvent(e):
                break;

            case PermissionRequestedEvent p:
                var card = QuestionItem.CanShow(p.Request)
                    ? new QuestionItem(p.Request, Folder, Answer)
                    : new PermissionItem(p.Request, Folder, Answer);
                Items.Add(card);
                PendingPermission = card;
                ContentChanged?.Invoke();
                _main.OnPermissionRequested(this);
                break;

            case PermissionCancelledEvent pc:
                foreach (var pending in Items.OfType<PermissionItem>().Where(i => i.Request.RequestId == pc.RequestId && !i.IsAnswered))
                {
                    pending.MarkAnswered($"{pending.Request.ToolName} request withdrawn");
                }

                PendingPermission = Items.OfType<PermissionItem>().LastOrDefault(i => !i.IsAnswered);
                break;

            case PermissionModeChangedEvent m when !_suppressModeSync:
                PermissionMode = m.Mode;
                break;

            case TurnCompletedEvent done:
                CompleteTurn(done.Result);
                break;

            case AgentErrorEvent err:
                Items.Add(new ErrorItem(err.Message));
                ContentChanged?.Invoke();
                break;

            case ProcessExitedEvent x:
                if (!x.Expected && x.ExitCode != 0 && !IsTurnRunning)
                {
                    var message = string.IsNullOrWhiteSpace(x.StderrTail)
                        ? $"Claude Code exited (code {x.ExitCode})."
                        : $"Claude Code exited (code {x.ExitCode}):\n{x.StderrTail}";
                    Items.Add(new ErrorItem(message));
                    ContentChanged?.Invoke();
                }

                _backend = null;
                ForgetBridgeProcess();
                FinishSubagents(includingBackground: true);
                FinishBackgroundTasks();
                _main.OnAliveChanged(this);
                _main.ReassertTitle(this);
                break;
        }
    }

    private void StartBlock(BlockStartedEvent b)
    {
        switch (b.Kind)
        {
            case AgentBlockKind.Text:
            case AgentBlockKind.Thinking:
                GetOrCreateBlock(b.MessageId, b.Index, b.Kind);
                break;
            case AgentBlockKind.ToolUse when b.ToolUseId is not null:
                if (!_calls.ContainsKey(b.ToolUseId))
                {
                    AddCall(b.ToolUseId, b.ToolName ?? "tool");
                    ContentChanged?.Invoke();
                }

                break;
        }
    }

    private TranscriptItem? GetOrCreateBlock(string messageId, int index, AgentBlockKind kind)
    {
        if (_blocks.TryGetValue((messageId, index), out var existing))
        {
            return existing;
        }

        TranscriptItem item;
        if (kind == AgentBlockKind.Thinking)
        {
            item = new ThinkingItem(messageId, index, "", streaming: true);
            AddThinking((ThinkingItem)item);
        }
        else
        {
            CloseGroup();
            item = new AssistantTextItem(messageId, index, "", streaming: true);
            Items.Add(item);
        }

        _blocks[(messageId, index)] = item;
        ContentChanged?.Invoke();
        return item;
    }

    /// <summary>
    /// As in the desktop app, only text ends an activity group: thinking joins the open group, or stands as its
    /// own line until a tool call opens a group and takes it in (<see cref="AddCall"/>).
    /// </summary>
    private void AddThinking(ThinkingItem thinking)
    {
        if (_openGroup is not null && Items.Contains(_openGroup))
        {
            _openGroup.AddThinking(thinking);
        }
        else
        {
            Items.Add(thinking);
        }
    }

    private void RemoveThinking(ThinkingItem thinking)
    {
        if (!Items.Remove(thinking))
        {
            foreach (var group in Items.OfType<ToolGroupItem>())
            {
                group.RemoveThinking(thinking);
            }
        }
    }

    private void CompleteBlock(BlockCompletedEvent c)
    {
        switch (c.Kind)
        {
            case AgentBlockKind.Text:
                if (GetOrCreateBlock(c.MessageId, c.Index, AgentBlockKind.Text) is AssistantTextItem text)
                {
                    if (c.Text is not null && c.Text != text.Text)
                    {
                        text.Text = c.Text;
                    }

                    text.IsStreaming = false;
                    if (string.IsNullOrWhiteSpace(text.Text))
                    {
                        Items.Remove(text);
                    }

                    ContentChanged?.Invoke();
                }

                break;
            case AgentBlockKind.Thinking:
                if (GetOrCreateBlock(c.MessageId, c.Index, AgentBlockKind.Thinking) is ThinkingItem thinking)
                {
                    if (!string.IsNullOrEmpty(c.Text))
                    {
                        thinking.Text = c.Text;
                    }

                    thinking.IsStreaming = false;
                    if (!thinking.HasText)
                    {
                        // Redacted / omitted thinking: nothing to expand, so no line at all.
                        RemoveThinking(thinking);
                    }
                }

                break;
        }
    }

    private ToolCallItem AddCall(string toolUseId, string name)
    {
        if (_calls.TryGetValue(toolUseId, out var existing))
        {
            return existing;
        }

        if (_openGroup is null || !Items.Contains(_openGroup))
        {
            // The thinking lines just before it become part of the group.
            _openGroup = new ToolGroupItem();
            var thoughts = new List<ThinkingItem>();
            while (Items.Count > 0 && Items[^1] is ThinkingItem thinking)
            {
                thoughts.Insert(0, thinking);
                Items.RemoveAt(Items.Count - 1);
            }

            foreach (var thinking in thoughts)
            {
                _openGroup.AddThinking(thinking);
            }

            Items.Add(_openGroup);
        }

        var call = new ToolCallItem(toolUseId, name, Folder) { Status = ToolStatus.Running };
        _openGroup.AddCall(call);
        _calls[toolUseId] = call;
        _groupOfCall[toolUseId] = _openGroup;
        _turnToolCount++;
        _openGroup.Refresh();
        return call;
    }

    private void CompleteCall(string toolUseId, bool isError, string text, System.Text.Json.JsonElement structured)
    {
        if (!_calls.TryGetValue(toolUseId, out var call))
        {
            return;
        }

        call.Status = isError ? ToolStatus.Failed : ToolStatus.Done;
        call.ResultText = text.Length > MaxResultChars ? text[..MaxResultChars] + "\n…" : text;
        (call.Added, call.Removed) = isError ? (0, 0) : ToolText.DiffStat(call.Name, structured);
        _groupOfCall[toolUseId].Refresh();

        // Files sent to the user show in the transcript itself, after the activity line (which the next tool reopens).
        if (call.Name == "SendUserFile" && !isError && new SentFilesItem(call.Input, call.Cwd) is { IsEmpty: false } sent)
        {
            CloseGroup();
            Items.Add(sent);
        }
    }

    private void FinishOpenCalls()
    {
        foreach (var call in _calls.Values.Where(c => c.Status == ToolStatus.Running))
        {
            call.Status = ToolStatus.Done;
            _groupOfCall[call.ToolUseId].Refresh();
        }
    }

    private void CloseGroup() => _openGroup = null;

    private void CompleteTurn(TurnResult result)
    {
        IsTurnRunning = false;
        FinishSubagents(includingBackground: false);
        foreach (var block in _blocks.Values)
        {
            switch (block)
            {
                case AssistantTextItem { IsStreaming: true } t:
                    t.IsStreaming = false;
                    break;
                case ThinkingItem { IsStreaming: true } th:
                    th.IsStreaming = false;
                    if (!th.HasText)
                    {
                        RemoveThinking(th);
                    }

                    break;
            }
        }

        _blocks.Clear();
        FinishOpenCalls();
        CloseGroup();

        foreach (var pending in Items.OfType<PermissionItem>().Where(i => !i.IsAnswered))
        {
            pending.MarkAnswered($"{pending.Request.ToolName} request withdrawn");
        }

        PendingPermission = null;

        if (result.Interrupted)
        {
            State = AgentState.Interrupted;
            Items.Add(new NoticeItem("Interrupted"));
        }
        else if (result.IsError)
        {
            State = AgentState.Error;
            Items.Add(new ErrorItem(result.ErrorMessage ?? "The turn failed."));
        }
        else
        {
            State = AgentState.Done;
        }

        var parts = new List<string> { ModelName };
        if (result.DurationMs > 0)
        {
            parts.Add("last turn " + Format.Duration(result.DurationMs));
        }

        if (result.OutputTokens > 0)
        {
            parts.Add(Format.Tokens(result.OutputTokens) + " tokens");
        }

        if (_turnToolCount > 0)
        {
            parts.Add(_turnToolCount == 1 ? "1 tool" : $"{_turnToolCount} tools");
        }

        AgentMeta = string.Join(" · ", parts);
        var context = result.ContextTokens;
        if (context > 0)
        {
            ContextTokens = context;
            ContextFraction = Math.Clamp(context / (double)Catalog.ContextWindow(Model), 0, 1);
        }

        ContentChanged?.Invoke();
        _main.OnTurnCompleted(this);

        // Claude may have edited, committed or switched branches: the git chip and the panel's git state are stale.
        _ = RefreshGitAsync();
        if (_main.IsSidePanelOpen && ReferenceEquals(_main.Current, this))
        {
            RefreshPanel(refresh: true);
        }

        // Queued prompts go out now; after a plain Stop they wait (for "Send now", or the next turn).
        if (Queued.Count > 0 && (!result.Interrupted || _sendQueuedOnStop))
        {
            Dispatcher.UIThread.Post(() => _ = SendQueuedAsync());
        }
    }

    private async void Answer(PermissionItem item, PermissionChoice choice)
    {
        if (item.IsAnswered)
        {
            return;
        }

        PermissionDecision decision;
        if (item is QuestionItem question)
        {
            if (choice == PermissionChoice.Deny)
            {
                item.MarkAnswered("Skipped the question");
                decision = new PermissionDecision(PermissionChoice.Deny, "The user skipped the question without answering.");
            }
            else if (question.CanSubmit)
            {
                item.MarkAnswered("Answered: " + question.AnswerSummary);
                decision = new PermissionDecision(PermissionChoice.Allow, UpdatedInput: question.BuildUpdatedInput());
            }
            else
            {
                return; // Enter before anything was chosen.
            }
        }
        else
        {
            var tool = item.Request.DisplayName ?? item.Request.ToolName;
            item.MarkAnswered(choice switch
            {
                PermissionChoice.Allow => $"Allowed {tool}",
                PermissionChoice.AllowForSession => $"Allowed {tool} for this session",
                _ => $"Denied {tool}",
            });
            decision = new PermissionDecision(choice);
        }

        PendingPermission = Items.OfType<PermissionItem>().LastOrDefault(i => !i.IsAnswered);

        if (_backend is null)
        {
            return;
        }

        try
        {
            await _backend.RespondAsync(item.Request, decision);
        }
        catch (Exception ex)
        {
            Items.Add(new ErrorItem("Could not send the answer: " + ex.Message));
        }
    }
}
