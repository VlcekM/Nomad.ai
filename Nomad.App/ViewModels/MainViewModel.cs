using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Infrastructure;
using Nomad.App.Updates;
using Nomad.Core.Agents;
using Nomad.Core.Git;
using Nomad.Core.OpenCode;
using Nomad.Core.Remote;
using Nomad.Core.Sessions;
using Nomad.Core.Settings;

namespace Nomad.App.ViewModels;

/// <summary>The window: sidebar (search, recents), the current session, settings, account.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly List<SessionListItemViewModel> _all = [];
    private readonly Dictionary<string, SessionViewModel> _open = new(StringComparer.Ordinal);
    private readonly string _settingsPath;
    private readonly Stack<SessionViewModel> _back = new();
    private readonly Stack<SessionViewModel> _forward = new();
    private bool _navigating;
    private CancellationTokenSource? _scanCts;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _usageTimer;

    /// <summary>Plan usage limits (the status-row ring's popup).</summary>
    public UsageViewModel Usage { get; } = new();

    /// <summary>What OpenCode spent this month (OpenCode sessions' usage), from its own records.</summary>
    public OpenCodeCostViewModel OpenCodeCost { get; } = new();

    /// <summary>The ChatGPT plan's Codex limits (Codex sessions' usage).</summary>
    public CodexUsageViewModel CodexUsage { get; } = new();

    public LocalTokenUsageViewModel LocalTokens { get; } = new();

    /// <summary>The session search (Ctrl+T).</summary>
    public SessionSearchViewModel Search { get; }

    /// <summary>Asks about files edited in the Files view before quitting or deleting their session.</summary>
    public UnsavedChangesViewModel Unsaved { get; } = new();

    /// <summary>Every file with unsaved edits, in every opened session.</summary>
    public IEnumerable<FileTab> EditedFiles => LiveSessions.SelectMany(s => s.Files.EditedFiles);

    /// <summary>A session whose CLI process has been quiet this long is ended and moves to Idle.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);

    public MainViewModel(NomadSettings settings, string settingsPath, DevOptions dev)
    {
        Settings = settings;
        _settingsPath = settingsPath;
        Dev = dev;
        // The session list is kept beside the settings between runs, so a start re-reads only the transcripts that changed.
        Store = new SessionStore(cachePath: Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? AppContext.BaseDirectory, "sessions-cache.json"));
        IsSidebarOpen = !settings.SidebarCollapsed;
        PlanLabel = string.IsNullOrEmpty(settings.LastPlan) ? null : settings.LastPlan;

        ApplyDisplayName();
        UpdateGroupingOptions();
        OpenCodeServer.CliPath = settings.OpenCodePath;
        Nomad.Core.Codex.CodexServer.CliPath = settings.CodexPath;
        if (dev.LogPath is { } log)
        {
            Nomad.Core.Codex.CodexServer.RawLine = (line, outgoing) => File.AppendAllText(log, (outgoing ? "> " : "< ") + line + Environment.NewLine);
        }
        SessionListItemViewModel.MainBackend = settings.MainBackend;

        Current = new SessionViewModel(this, dev.Cwd ?? settings.LastFolder, dev.Backend is null ? null : AgentBackends.Normalize(dev.Backend));
        if (!dev.IsActive && !string.IsNullOrEmpty(settings.NewSessionDraft))
        {
            Current.ComposerText = settings.NewSessionDraft;
        }

        IsSidePanelOpen = settings.SidePanelOpen || dev.SidePanel;
        if (dev.Model is not null)
        {
            Current.Model = dev.Model;
            Current.Effort = EffortFor(dev.Model);
        }

        if (Current.IsOpenCode)
        {
            _ = LoadOpenCodeCatalogAsync();
        }
        else if (Current.IsCodex)
        {
            _ = LoadCodexCatalogAsync();
        }

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _idleTimer.Tick += (_, _) =>
        {
            SleepIdleSessions();
            _ = LoadWorktreeMergesAsync(); // A merge made outside Nomad.ai (or in a turn that just ended) still idles its session.
            _ = AutoArchiveSessionsAsync(fold: true);
            Current.TickClock();
        };
        _idleTimer.Start();

        _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _usageTimer.Tick += (_, _) =>
        {
            _ = Usage.RefreshAsync();
            if (Current.IsNew || !Current.IsClaudeCode)
            {
                _ = RefreshVisibleUsageAsync();
            }
        };
        _usageTimer.Start();
        _ = Usage.RefreshAsync(force: true);
        if (Current.IsNew || !Current.IsClaudeCode)
        {
            _ = RefreshVisibleUsageAsync(force: true);
        }

        _phoneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _phoneTimer.Tick += (_, _) => _ = ScanPhoneSessionsAsync();

        // Sessions another Nomad window or a terminal is running: alive here too, but theirs to run.
        _elsewhereTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _elsewhereTimer.Tick += (_, _) => _ = ScanElsewhereAsync();
        _elsewhereTimer.Start();

        Hub.Start(); // Nomad Hub, when this PC is enrolled.

        // The open session's branch, unpushed commits and uncommitted changes, which other sessions and terminals change too.
        _gitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _gitTimer.Tick += (_, _) => _ = Current.RefreshGitAsync();
        _gitTimer.Start();

        // Scripted runs leave the folders to the user's own instance (one server per folder).
        if (!dev.IsActive || dev.Phone)
        {
            foreach (var folder in settings.PhoneFolders)
            {
                StartPhoneServer(folder);
            }
        }

        Search = new SessionSearchViewModel(this);
        Updates = new UpdateService(this);

        // The list the last run saw, there in the window's first frame; the scan at start brings it up to date.
        MergeSummaries(Store.LoadCache());
    }

    // ------------------------------------------------------------------ phone (Remote Control servers)

    private readonly Dictionary<string, RemoteControlServer> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _phoneTimer;
    private readonly DispatcherTimer _gitTimer;

    // Running phone sessions by claude.ai id, with the transcript they write and its session id.
    private readonly Dictionary<string, (PhoneSessionProcess Process, string File, string SessionId)> _phone = new(StringComparer.Ordinal);
    private bool _phoneScanning;

    /// <summary>Raised when a folder's server changed state (the settings page shows it).</summary>
    public event Action<string>? PhoneServerChanged;

    /// <summary>A Remote Control server process is running (closing waits for it to stop).</summary>
    public bool HasPhoneServers => _servers.Values.Any(s => s.ProcessId is not null);

    public RemoteControlServer? PhoneServer(string folder) => _servers.GetValueOrDefault(folder.TrimEnd('\\', '/'));

    public void AddPhoneFolder(string folder)
    {
        folder = folder.TrimEnd('\\', '/');
        if (Settings.PhoneFolders.Any(f => string.Equals(f.TrimEnd('\\', '/'), folder, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Settings.PhoneFolders.Add(folder);
        SaveSettings();
        StartPhoneServer(folder);
    }

    public async Task RemovePhoneFolderAsync(string folder)
    {
        folder = folder.TrimEnd('\\', '/');
        Settings.PhoneFolders.RemoveAll(f => string.Equals(f.TrimEnd('\\', '/'), folder, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        if (_servers.Remove(folder, out var server))
        {
            await server.StopAsync();
        }

        UpdatePhoneTimer();
    }

    /// <summary>Tries a folder's server again (after the one-time setup in a terminal).</summary>
    public void RetryPhoneServer(string folder) => StartPhoneServer(folder);

    private void StartPhoneServer(string folder)
    {
        folder = folder.TrimEnd('\\', '/');
        if (!_servers.TryGetValue(folder, out var server))
        {
            var mode = Settings.DefaultPermissionMode;
            server = new RemoteControlServer(folder, Settings.CliPath, mode);
            server.Changed += () => Post(() =>
            {
                UpdatePhoneTimer();
                PhoneServerChanged?.Invoke(folder);
            });
            _servers[folder] = server;
        }

        server.Start();
        UpdatePhoneTimer();
    }

    private void UpdatePhoneTimer()
    {
        var any = _servers.Values.Any(s => s.State is RemoteControlServerState.Running or RemoteControlServerState.Starting) || _phone.Count > 0;
        if (any)
        {
            _phoneTimer.Start();
        }
        else
        {
            _phoneTimer.Stop();
        }
    }

    /// <summary>Finds sessions the servers started for the phone and shows them as alive.</summary>
    private async Task ScanPhoneSessionsAsync()
    {
        if (_phoneScanning)
        {
            return;
        }

        _phoneScanning = true;
        try
        {
            var folders = _servers.Values.Where(s => s.ProcessId is not null).ToDictionary(s => s.ProcessId!.Value, s => s.Folder);
            var known = _phone.ToDictionary(p => p.Key, p => p.Value.File, StringComparer.Ordinal);
            var root = Store.ProjectsRoot;
            var found = await Task.Run(() =>
            {
                var result = new List<(PhoneSessionProcess Process, string File)>();
                var taken = new HashSet<string>(known.Values, StringComparer.OrdinalIgnoreCase);
                foreach (var p in RemoteSessionScanner.Scan().OrderBy(p => p.StartedUtc))
                {
                    if (known.TryGetValue(p.RemoteId, out var file))
                    {
                        result.Add((p, file));
                    }
                    else if (folders.TryGetValue(p.ParentProcessId, out var folder)
                        && RemoteSessionScanner.FindTranscript(root, folder, p.StartedUtc, taken) is { } newFile)
                    {
                        taken.Add(newFile);
                        result.Add((p, newFile));
                    }
                }

                return result;
            });

            var changed = false;
            var alive = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (process, file) in found)
            {
                alive.Add(process.RemoteId);
                if (!_phone.ContainsKey(process.RemoteId))
                {
                    _phone[process.RemoteId] = (process, file, Path.GetFileNameWithoutExtension(file));
                    changed = true;
                }
            }

            foreach (var gone in _phone.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                var sessionId = _phone[gone].SessionId;
                _phone.Remove(gone);
                if (_open.TryGetValue(sessionId, out var session))
                {
                    session.SetPhoneSession(null, null);
                }

                changed = true;
            }

            if (changed)
            {
                await RefreshSessionsAsync();
                foreach (var (process, file, sessionId) in _phone.Values)
                {
                    if (_open.TryGetValue(sessionId, out var session) && !session.IsPhoneSession)
                    {
                        session.SetPhoneSession(process, file);
                    }
                }
            }

            UpdatePhoneTimer();
        }
        finally
        {
            _phoneScanning = false;
        }
    }

    private bool IsPhone(string sessionId) => _phone.Values.Any(p => p.SessionId == sessionId);

    // ------------------------------------------------------------------ sessions running elsewhere

    private readonly DispatcherTimer _elsewhereTimer;
    private IReadOnlySet<string> _elsewhere = new HashSet<string>();
    private bool _elsewhereScanning;

    /// <summary>A Claude Code process outside this Nomad (another window, a terminal) runs the session.</summary>
    public bool IsRunningElsewhere(string sessionId) => _elsewhere.Contains(sessionId) && !(_open.TryGetValue(sessionId, out var s) && s.IsProcessRunning);

    private async Task ScanElsewhereAsync()
    {
        if (_elsewhereScanning)
        {
            return;
        }

        _elsewhereScanning = true;
        try
        {
            var found = await Task.Run(RemoteSessionScanner.RunningSessionIds);
            if (!found.SetEquals(_elsewhere))
            {
                _elsewhere = found;
                ApplyElsewhere();
            }
        }
        finally
        {
            _elsewhereScanning = false;
        }
    }

    private void ApplyElsewhere()
    {
        var changed = false;
        foreach (var vm in _all)
        {
            var elsewhere = IsRunningElsewhere(vm.SessionId);
            var alive = (_open.TryGetValue(vm.SessionId, out var live) && live.IsProcessRunning) || vm.IsPhone || elsewhere;
            changed |= vm.IsElsewhere != elsewhere || vm.IsAlive != alive;
            vm.IsElsewhere = elsewhere;
            vm.IsAlive = alive;
        }

        if (changed)
        {
            ApplyFilter();
        }
    }

    /// <summary>A phone session was taken over by Nomad ("Continue here").</summary>
    public void OnPhoneSessionEnded(SessionViewModel session)
    {
        if (session.SessionId is not { } id)
        {
            return;
        }

        foreach (var key in _phone.Where(p => p.Value.SessionId == id).Select(p => p.Key).ToList())
        {
            _phone.Remove(key);
        }

        if (_all.FirstOrDefault(s => s.SessionId == id) is { } vm)
        {
            vm.IsPhone = false;
            vm.IsAlive = session.IsProcessRunning;
            ApplyFilter();
        }
    }

    public NomadSettings Settings { get; }

    public DevOptions Dev { get; }

    public SessionStore Store { get; }

    /// <summary>Self-update (Velopack) and the GitHub release list.</summary>
    public UpdateService Updates { get; }

    /// <summary>Sessions with a running CLI process: rows, under <see cref="SessionGroupHeader"/>s if grouped.</summary>
    public ObservableCollection<object> Alive { get; } = [];

    /// <summary>All other sessions (the Idle list): rows, under <see cref="SessionGroupHeader"/>s unless grouping is off.</summary>
    public ObservableCollection<object> Recents { get; } = [];

    /// <summary>The sliders menu's "Group by" choices.</summary>
    public IReadOnlyList<MenuOption> GroupingOptions { get; } =
    [
        new("Date", "Date"),
        new("Recent", "Hours, then days"),
        new("Folder", "Folder"),
        new("None", "None"),
    ];

    /// <summary>The Alive heading's sliders menu's "Group by" choices.</summary>
    public IReadOnlyList<MenuOption> AliveGroupingOptions { get; } =
    [
        new("Date", "Date"),
        new("Recent", "Hours, then days"),
        new("Folder", "Folder"),
        new("None", "None"),
    ];

    /// <summary>Alive rows show their project on a second line.</summary>
    public bool ShowAliveProject
    {
        get => Settings.AliveShowProject;
        set
        {
            if (Settings.AliveShowProject != value)
            {
                Settings.AliveShowProject = value;
                SaveSettings();
                OnPropertyChanged();
                ApplyFilter();
            }
        }
    }

    public bool ShowArchived
    {
        get => Settings.ShowArchived;
        set
        {
            if (Settings.ShowArchived != value)
            {
                Settings.ShowArchived = value;
                SaveSettings();
                OnPropertyChanged();
                ApplyFilter();
            }
        }
    }

    // ------------------------------------------------------------------ slash commands

    // Before any Claude Code ran (so nothing is known yet): its commands that work in a Nomad session.
    private static readonly Nomad.Core.Protocol.SlashCommand[] FallbackCommands =
    [
        new() { Name = "compact", Description = "Clear the conversation but keep a summary in context", ArgumentHint = "<optional custom summarization instructions>" },
        new() { Name = "context", Description = "Show what fills the context window" },
        new() { Name = "init", Description = "Initialize a CLAUDE.md file with codebase documentation" },
        new() { Name = "review", Description = "Review a pull request" },
        new() { Name = "security-review", Description = "Complete a security review of the pending changes on the current branch" },
        new() { Name = "usage", Description = "Show plan usage limits" },
    ];

    /// <summary>The "/" menu's commands: the latest list a Claude Code process reported.</summary>
    public IReadOnlyList<Nomad.Core.Protocol.SlashCommand> SlashCommands =>
        Settings.SlashCommands.Count > 0 ? Settings.SlashCommands : FallbackCommands;

    public void OnSlashCommands(IReadOnlyList<Nomad.Core.Protocol.SlashCommand> commands)
    {
        var names = commands.Select(c => c.Name);
        if (!names.SequenceEqual(Settings.SlashCommands.Select(c => c.Name))
            || !commands.Select(c => c.Description).SequenceEqual(Settings.SlashCommands.Select(c => c.Description)))
        {
            Settings.SlashCommands = commands.ToList();
            SaveSettings();
        }
    }

    // ------------------------------------------------------------------ sidebar width and activity lines

    public const double MinSidebarWidth = 240;
    public const double MaxSidebarWidth = 560;

    /// <summary>The sidebar's width while open (its right edge drags it).</summary>
    public double SidebarWidth
    {
        get => Math.Clamp(Settings.SidebarWidth, MinSidebarWidth, MaxSidebarWidth);
        set
        {
            value = Math.Clamp(Math.Round(value), MinSidebarWidth, MaxSidebarWidth);
            if (Math.Abs(Settings.SidebarWidth - value) > 0.1)
            {
                Settings.SidebarWidth = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SidebarContentWidth));
            }
        }
    }

    /// <summary>The sidebar's content, one pixel narrower than it (its right border).</summary>
    public double SidebarContentWidth => SidebarWidth - 1;

    public const double DefaultSidePanelWidth = 296;

    /// <summary>The side panel's width while open (its left edge drags it).</summary>
    public double SidePanelWidth
    {
        get => Math.Clamp(Settings.SidePanelWidth, MinSidebarWidth, MaxSidebarWidth);
        set
        {
            value = Math.Clamp(Math.Round(value), MinSidebarWidth, MaxSidebarWidth);
            if (Math.Abs(Settings.SidePanelWidth - value) > 0.1)
            {
                Settings.SidePanelWidth = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Thinking and tool lines in the transcript; off, only prompts and replies are shown.</summary>
    public bool ShowActivity
    {
        get => Settings.ShowActivity;
        set
        {
            if (Settings.ShowActivity != value)
            {
                Settings.ShowActivity = value;
                SaveSettings();
                OnPropertyChanged();
                foreach (var session in _open.Values.Append(Current).Distinct())
                {
                    session.OnShowActivityChanged();
                }
            }
        }
    }

    /// <summary>The usage ring at the end of the status row.</summary>
    public bool ShowUsageRing
    {
        get => Settings.ShowUsageRing;
        set
        {
            if (Settings.ShowUsageRing != value)
            {
                Settings.ShowUsageRing = value;
                SaveSettings();
                OnPropertyChanged();
                foreach (var session in _open.Values.Append(Current).Distinct())
                {
                    session.OnShowUsageRingChanged();
                }
            }
        }
    }

    /// <summary>The provider usage cards above recent projects on the new-session screen.</summary>
    public bool ShowNewSessionUsage
    {
        get => Settings.ShowNewSessionUsage;
        set
        {
            if (Settings.ShowNewSessionUsage == value)
            {
                return;
            }

            Settings.ShowNewSessionUsage = value;
            SaveSettings();
            OnPropertyChanged();
            foreach (var session in _open.Values.Append(Current).Distinct())
            {
                session.OnShowNewSessionUsageChanged();
            }

            if (value && Current.IsNew)
            {
                _ = RefreshVisibleUsageAsync();
            }
        }
    }

    /// <summary>Only the current model's chip on the status row; the rest are in its menu.</summary>
    public bool CompactModelPicker
    {
        get => Settings.CompactModelPicker;
        set
        {
            if (Settings.CompactModelPicker != value)
            {
                Settings.CompactModelPicker = value;
                SaveSettings();
                OnPropertyChanged();
                foreach (var session in _open.Values.Append(Current).Distinct())
                {
                    session.OnCompactModelPickerChanged();
                }
            }
        }
    }

    public void SetGrouping(string id)
    {
        Settings.SessionGrouping = id;
        SaveSettings();
        UpdateGroupingOptions();
        ApplyFilter();
    }

    public void SetAliveGrouping(string id)
    {
        Settings.AliveGrouping = id;
        SaveSettings();
        UpdateGroupingOptions();
        ApplyFilter();
    }

    private void UpdateGroupingOptions()
    {
        var current = GroupingOptions.Any(o => o.Id == Settings.SessionGrouping) ? Settings.SessionGrouping : "Date";
        foreach (var option in GroupingOptions)
        {
            option.IsSelected = option.Id == current;
        }

        var alive = AliveGroupingOptions.Any(o => o.Id == Settings.AliveGrouping) ? Settings.AliveGrouping : "None";
        foreach (var option in AliveGroupingOptions)
        {
            option.IsSelected = option.Id == alive;
        }
    }

    [ObservableProperty]
    public partial bool HasAlive { get; set; }

    [ObservableProperty]
    public partial int AliveCount { get; set; }

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string UserInitials { get; set; } = "";

    public string SettingsPath => _settingsPath;

    /// <summary>The open settings dialog's model (null while closed).</summary>
    [ObservableProperty]
    public partial SettingsViewModel? SettingsPage { get; set; }

    /// <summary>Footer name: the Display name setting, else the Windows user name.</summary>
    public void ApplyDisplayName()
    {
        UserName = string.IsNullOrWhiteSpace(Settings.DisplayName) ? Format.Capitalize(Environment.UserName) : Settings.DisplayName;
        UserInitials = Initials(UserName);
    }

    public string DefaultModel => Dev.Model ?? Settings.DefaultModel;

    /// <summary>
    /// The effort a session starts with on a model: the one last chosen with it, else high, except Opus 5.5, which
    /// starts at the Default effort setting. An OpenCode model starts at its last one while it still has that variant,
    /// else at OpenCode's default for it; a Codex model likewise with its reasoning efforts.
    /// </summary>
    public string EffortFor(string model) =>
        Nomad.Core.OpenCode.OpenCodeBackend.IsOpenCodeModel(model)
            ? Settings.EffortByModel.TryGetValue(model.ToLowerInvariant(), out var variant)
                && (OpenCodeCatalog.Find(model) is not { } m || m.Variants.Contains(variant)) ? variant : ""
        : CodexCatalog.IsCodexModel(model)
            ? Settings.EffortByModel.TryGetValue(model.ToLowerInvariant(), out var level)
                && (CodexCatalog.Find(model) is not { } c || c.Efforts.Contains(level)) ? level : ""
        : Settings.EffortByModel.TryGetValue(model.ToLowerInvariant(), out var effort) ? effort
        : model.StartsWith("claude-opus-5-5", StringComparison.OrdinalIgnoreCase) ? Settings.DefaultEffort
        : "high";

    public void RememberEffort(string model, string effort)
    {
        var key = model.ToLowerInvariant();
        if (Settings.EffortByModel.GetValueOrDefault(key) != effort)
        {
            Settings.EffortByModel[key] = effort;
            SaveSettings();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewSelected))]
    [NotifyPropertyChangedFor(nameof(ShowSidePanel))]
    public partial SessionViewModel Current { get; set; }

    public bool IsNewSelected => Current.IsNew;

    /// <summary>
    /// The new-session page while another session is shown, so what was typed (and attached, and the folder and
    /// model picked) there is still there on coming back to New.
    /// </summary>
    private SessionViewModel? _draft;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    /// <summary>The settings dialog is on screen: from opening until its fade-out ends.</summary>
    [ObservableProperty]
    public partial bool IsSettingsShown { get; set; }

    /// <summary>Faded in: set a frame after <see cref="IsSettingsShown"/>, so the fade runs from nothing.</summary>
    [ObservableProperty]
    public partial bool IsSettingsRaised { get; set; }

    /// <summary>The right-hand panel: project, context, plan usage and MCP servers (Ctrl+Shift+B).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSidePanel))]
    public partial bool IsSidePanelOpen { get; set; }

    /// <summary>
    /// The side panel is shown: open, and not on the new-session page (it slides in once the session starts) nor
    /// in the Files view (whose tree may be on the same side).
    /// </summary>
    public bool ShowSidePanel => IsSidePanelOpen && !Current.IsNew && !Current.IsFilesView;

    /// <summary>In the Files view, the tree is on the right and the file on the left; off, the other way round.</summary>
    public bool FileTreeOnRight
    {
        get => Settings.FileTreeOnRight;
        set
        {
            if (Settings.FileTreeOnRight != value)
            {
                Settings.FileTreeOnRight = value;
                SaveSettings();
                OnPropertyChanged();
                foreach (var session in _open.Values.Append(Current).Distinct())
                {
                    session.OnFileTreeSideChanged();
                }
            }
        }
    }

    /// <summary>The key that switches between Chat and Files (Tab unless rebound in the settings).</summary>
    public KeyGesture? FilesViewGesture
    {
        get
        {
            try
            {
                return string.IsNullOrWhiteSpace(Settings.FilesViewKey) ? null : KeyGesture.Parse(Settings.FilesViewKey);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    public string FilesViewKey
    {
        get => Settings.FilesViewKey;
        set
        {
            if (Settings.FilesViewKey != value)
            {
                Settings.FilesViewKey = value;
                SaveSettings();
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilesViewGesture));
                foreach (var session in _open.Values.Append(Current).Distinct())
                {
                    session.OnFilesViewKeyChanged();
                }
            }
        }
    }

    public const double DefaultFileTreeWidth = 280;

    /// <summary>The Files view's tree width (its inner edge drags it).</summary>
    public double FileTreeWidth
    {
        get => Math.Clamp(Settings.FileTreeWidth, 180, 720);
        set
        {
            value = Math.Clamp(Math.Round(value), 180, 720);
            if (Math.Abs(Settings.FileTreeWidth - value) > 0.1)
            {
                Settings.FileTreeWidth = value;
                OnPropertyChanged();
            }
        }
    }

    partial void OnIsSidePanelOpenChanged(bool value)
    {
        if (value)
        {
            Current.RefreshPanel();
            _ = Current.Usage.RefreshAsync();
        }

        if (Settings.SidePanelOpen != value && !Dev.IsActive)
        {
            Settings.SidePanelOpen = value;
            SaveSettings();
        }
    }

    [RelayCommand]
    public void ToggleSidePanel() => IsSidePanelOpen = !IsSidePanelOpen;

    private readonly Dictionary<string, ProjectViewModel> _projects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One shared project per folder, so its git state loads once for the cards and the panel.</summary>
    public ProjectViewModel ProjectFor(string folder)
    {
        var key = folder.TrimEnd('\\', '/');
        if (!_projects.TryGetValue(key, out var project))
        {
            _projects[key] = project = new ProjectViewModel(folder);
        }

        return project;
    }

    /// <summary>What a folder is called: its name, or for a worktree its repository's name.</summary>
    public static string ProjectName(string? folder) =>
        Format.FolderName(folder is null ? null : GitInfo.WorktreeOwner(folder) ?? folder);

    /// <summary>The latest activity of any listed session in this folder.</summary>
    public DateTime? LastActivityIn(string folder)
    {
        var key = folder.TrimEnd('\\', '/');
        var times = _all.Where(s => s.WorkingDirectory is { } w && string.Equals((GitInfo.WorktreeOwner(w) ?? w).TrimEnd('\\', '/'), key, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.LastActivityUtc).ToList();
        return times.Count > 0 ? times.Max() : null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    public partial string? PlanLabel { get; set; }

    public bool HasPlan => !string.IsNullOrEmpty(PlanLabel);

    [ObservableProperty]
    public partial double TranscriptMaxWidth { get; set; } = 1280;

    [ObservableProperty]
    public partial bool HasNoSessions { get; set; }

    /// <summary>Folders for the folder menu and the new-session card: settings first, then from transcripts.</summary>
    public IReadOnlyList<string> RecentFolders
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var folder in Settings.RecentFolders.Concat(_all.Select(s => s.WorkingDirectory).OfType<string>()))
            {
                var f = GitInfo.WorktreeOwner(folder) ?? folder; // A worktree's sessions count for its repository.
                if (seen.Add(f.TrimEnd('\\', '/')) && !IsScratch(f) && Directory.Exists(f))
                {
                    list.Add(f);
                }

                if (list.Count >= 12)
                {
                    break;
                }
            }

            return list;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    partial void OnIsSidebarOpenChanged(bool value)
    {
        Settings.SidebarCollapsed = !value;
        SaveSettings();
    }

    // Leaving a session while its title is being edited keeps the new name.
    partial void OnCurrentChanging(SessionViewModel value) => EndTitleRename(save: true);

    // The new-session dashboard shows all three providers, independent of the selected backend.
    private Task RefreshVisibleUsageAsync(bool force = false) => Current.IsNew && ShowNewSessionUsage
        ? Task.WhenAll(Usage.RefreshAsync(force), CodexUsage.RefreshAsync(force), OpenCodeCost.RefreshAsync(force), LocalTokens.RefreshAsync(force))
        : Current.Usage.RefreshAsync(force);

    partial void OnCurrentChanged(SessionViewModel oldValue, SessionViewModel newValue)
    {
        newValue?.ApplyDefaultMode();
        _ = RefreshVisibleUsageAsync();

        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnCurrentPropertyChanged;

            // Back/forward history; an untouched new session is not worth going back to.
            if (!_navigating && !(oldValue.IsNew && oldValue.Items.Count == 0))
            {
                _back.Push(oldValue);
                _forward.Clear();
            }

            if (oldValue.IsNew && oldValue.Items.Count == 0)
            {
                _draft = oldValue;
                SaveDraft();
            }
        }

        if (ReferenceEquals(newValue, _draft))
        {
            _draft = null;
        }

        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        newValue.RefreshFolders();
        if (IsSidePanelOpen)
        {
            newValue.RefreshPanel();
        }

        newValue.PropertyChanged += OnCurrentPropertyChanged;
        foreach (var item in _all)
        {
            item.IsSelected = newValue.SessionId is not null && item.SessionId == newValue.SessionId;
            if (item.IsSelected)
            {
                item.IsUnread = false;
            }
        }
    }

    private void OnCurrentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsFilesView))
        {
            OnPropertyChanged(nameof(ShowSidePanel));
        }

        if (e.PropertyName == nameof(SessionViewModel.IsNew))
        {
            OnPropertyChanged(nameof(IsNewSelected));
            OnPropertyChanged(nameof(ShowSidePanel));

            // The draft went out as a prompt: nothing to bring back after a restart.
            if (!Current.IsNew)
            {
                SaveDraft();
            }
        }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    public void NewSession()
    {
        if (Current.IsNew)
        {
            return;
        }

        Current = _draft is { IsNew: true } draft ? draft : new SessionViewModel(this, Settings.LastFolder);
    }

    /// <summary>Pins a folder to the Alive list (grouping it by folder, where pinned folders show) or unpins it.</summary>
    public void ToggleAliveFolder(string folder)
    {
        folder = folder.TrimEnd('\\', '/');
        bool Same(string f) => string.Equals(f.TrimEnd('\\', '/'), folder, StringComparison.OrdinalIgnoreCase);
        if (Settings.AliveFolders.RemoveAll(Same) == 0)
        {
            Settings.AliveFolders.Add(folder);
            if (Settings.AliveGrouping != "Folder")
            {
                Settings.AliveGrouping = "Folder";
                UpdateGroupingOptions();
            }

            if (IsAliveCollapsed)
            {
                ToggleAliveSection();
            }
        }

        SaveSettings();
        ApplyFilter();
    }

    public bool IsAliveFolder(string folder) =>
        Settings.AliveFolders.Any(f => string.Equals(f.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>An Alive folder group's +: the new-session page, with that project picked (null for no folder).</summary>
    public void NewSessionIn(string? folder)
    {
        NewSession();
        Current.SetFolder(folder);
    }

    /// <summary>The default mode changed in the settings: the new-session page follows, unless a mode was picked there.</summary>
    public void OnDefaultModeChanged()
    {
        foreach (var session in LiveSessions.Append(_draft).OfType<SessionViewModel>().Distinct())
        {
            session.ApplyDefaultMode();
        }
    }

    /// <summary>The new-session page, shown or kept aside: its text is saved for the next start.</summary>
    private SessionViewModel? DraftSession => Current.IsNew && Current.Items.Count == 0 ? Current : _draft;

    /// <summary>Keeps the new-session page's text in the settings, so it survives a restart.</summary>
    private void SaveDraft()
    {
        var text = DraftSession is { IsNew: true } draft ? draft.ComposerText : "";
        if (Dev.IsActive || (Settings.NewSessionDraft ?? "") == text)
        {
            return;
        }

        Settings.NewSessionDraft = text.Length > 0 ? text : null;
        SaveSettings();
    }

    [RelayCommand]
    public async Task OpenSessionAsync(SessionListItemViewModel item)
    {
        if (_open.TryGetValue(item.SessionId, out var live))
        {
            Current = live;
            return;
        }

        if (!item.IsStored)
        {
            return;
        }

        var session = CreateStoredSession(item);
        if (_phone.Values.FirstOrDefault(p => p.SessionId == item.SessionId) is { File: not null } phone)
        {
            session.SetPhoneSession(phone.Process, phone.File);
        }

        Current = session;
        await LoadHistoryAsync(session, item);
    }

    /// <summary>Moves a session between Alive and Idle: ends its CLI process, or starts one that resumes it.</summary>
    public async Task ToggleAliveAsync(SessionListItemViewModel item)
    {
        if (!item.CanManage)
        {
            return; // Its process belongs to the phone.
        }

        if (_open.TryGetValue(item.SessionId, out var session) && session.IsProcessRunning)
        {
            await session.SleepAsync();
            return;
        }

        if (session is null)
        {
            if (!item.IsStored)
            {
                return;
            }

            session = CreateStoredSession(item);
            await LoadHistoryAsync(session, item);
        }

        await session.WakeAsync();
    }

    /// <summary>
    /// Fork after <paramref name="message"/>'s turn: a new session with the conversation up to there,
    /// opened at once. It is written (with a new id, <c>--fork-session</c>) when its first prompt goes
    /// out; the original stays as it is.
    /// </summary>
    public void Fork(SessionViewModel source, UserMessageItem message)
    {
        var index = source.Items.IndexOf(message);
        if (source.SessionId is not { } sessionId || index < 0 || source.IsTurnRunning)
        {
            return;
        }

        // Keep through the end of this turn: up to the record before the next prompt, or everything.
        var next = source.Items.Skip(index + 1).OfType<UserMessageItem>().FirstOrDefault();
        string? resumeAt = null;
        if (next is not null)
        {
            resumeAt = next.ParentUuid ?? (next.Uuid is { } uuid ? source.ParentInTranscript(uuid) : null);
            if (resumeAt is null)
            {
                return;
            }
        }

        var end = next is null ? source.Items.Count : source.Items.IndexOf(next);
        var fork = new SessionViewModel(this, source.Folder, source.Backend)
        {
            Title = $"{source.Title} (fork)",
            IsNew = false,
            Model = source.Model,
            PermissionMode = source.PermissionMode,
            Effort = source.Effort,
        };
        fork.PrepareFork(sessionId, resumeAt, source.Items.Take(end));
        Current = fork;
    }

    private void SleepIdleSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var session in _open.Values.Append(Current).Distinct().ToList())
        {
            // Remote Control sessions stay up: without a process the phone cannot reach them. So do ones with a subagent
            // or background command still at work, which would die with the process, and pinned ones.
            if (session.IsProcessRunning && !session.IsTurnRunning && !session.HasRemoteControlUrl && !session.HasBackgroundWork
                && !(session.SessionId is { } id && Settings.PinnedSessions.Contains(id))
                && now - session.LastActivityUtc > IdleTimeout)
            {
                _ = session.SleepAsync();
            }
        }
    }

    private SessionViewModel CreateStoredSession(SessionListItemViewModel item)
    {
        var session = new SessionViewModel(this, item.WorkingDirectory, item.Backend) { IsLoadingHistory = true, Title = item.Title, IsNew = false, SessionId = item.SessionId };
        if (item.IsOpenCode)
        {
            _ = LoadOpenCodeCatalogAsync();
        }
        else if (item.IsCodex)
        {
            _ = LoadCodexCatalogAsync();
        }

        _open[item.SessionId] = session;
        return session;
    }

    private static async Task LoadHistoryAsync(SessionViewModel session, SessionListItemViewModel item)
    {
        var summary = new SessionSummary(item.SessionId, item.FilePath ?? "", item.WorkingDirectory, item.Title, item.LastActivityUtc, 0, Backend: item.Backend);
        try
        {
            var history = item.IsOpenCode ? await LoadOpenCodeHistoryAsync(item)
                : item.IsCodex ? await LoadCodexHistoryAsync(item)
                : await Task.Run(() => SessionStore.LoadHistory(item.FilePath!));
            session.LoadHistory(summary, history);
        }
        catch (Exception ex)
        {
            session.Items.Add(new ErrorItem("Could not read this session: " + ex.Message));
        }
        finally
        {
            session.IsLoadingHistory = false;
        }
    }

    private static async Task<SessionHistory> LoadOpenCodeHistoryAsync(SessionListItemViewModel item)
    {
        var server = await OpenCodeServer.GetAsync();
        using var messages = await OpenCodeClient.GetMessagesAsync(server, item.WorkingDirectory, item.SessionId);
        return OpenCodeHistory.FromMessages(messages.RootElement, item.SessionId, item.WorkingDirectory);
    }

    private static async Task<SessionHistory> LoadCodexHistoryAsync(SessionListItemViewModel item)
    {
        var server = await Nomad.Core.Codex.CodexServer.GetAsync();
        var result = await Nomad.Core.Codex.CodexClient.ReadThreadAsync(server, item.SessionId);
        return Nomad.Core.Codex.CodexHistory.FromThread(result.GetProperty("thread"), item.SessionId, item.WorkingDirectory);
    }

    /// <summary>Opens a stored session by id (dev hook <c>--dev-session</c>).</summary>
    public async Task OpenSessionByIdAsync(string sessionId)
    {
        await AwaitOpenCodeSessionsAsync([sessionId]);
        if (FindItem(sessionId) is { } item)
        {
            await OpenSessionAsync(item);
        }
    }

    /// <summary>A session's sidebar row, or one made from its transcript if the list doesn't have it.</summary>
    private SessionListItemViewModel? FindItem(string sessionId)
    {
        var item = _all.FirstOrDefault(s => s.SessionId == sessionId);
        if (item is null && Store.FindTranscript(sessionId) is { } file && Store.GetSummary(file) is { } summary)
        {
            item = new SessionListItemViewModel(summary.SessionId)
            {
                FilePath = summary.FilePath,
                Title = summary.Title,
                WorkingDirectory = summary.WorkingDirectory,
                LastActivityUtc = summary.LastActivityUtc,
            };
        }

        return item;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Navigate(_back, _forward);

    private bool CanGoBack() => _back.Count > 0;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => Navigate(_forward, _back);

    private bool CanGoForward() => _forward.Count > 0;

    private void Navigate(Stack<SessionViewModel> from, Stack<SessionViewModel> to)
    {
        if (from.Count == 0)
        {
            return;
        }

        var target = from.Pop();
        if (!(Current.IsNew && Current.Items.Count == 0))
        {
            to.Push(Current);
        }

        _navigating = true;
        try
        {
            Current = target;
        }
        finally
        {
            _navigating = false;
        }

        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    public void OpenSettings()
    {
        if (IsSettingsOpen)
        {
            return;
        }

        SettingsPage?.Detach();
        SettingsPage = new SettingsViewModel(this);
        IsSettingsOpen = true;
        IsSettingsShown = true;
        Dispatcher.UIThread.Post(() => IsSettingsRaised = IsSettingsOpen, DispatcherPriority.Loaded);
    }

    [RelayCommand]
    public void CloseSettings()
    {
        if (!IsSettingsOpen)
        {
            return;
        }

        IsSettingsOpen = false;
        IsSettingsRaised = false;
        SettingsPage?.Detach();
        SaveSettings();
        // The dialog keeps its page while it fades out.
        DispatcherTimer.RunOnce(() =>
        {
            if (!IsSettingsOpen)
            {
                IsSettingsShown = false;
                SettingsPage = null;
            }
        }, TimeSpan.FromMilliseconds(220));
    }

    // ------------------------------------------------------------------ sessions list

    /// <summary>Rescans <c>~/.claude/projects</c> on a background thread and merges the result.</summary>
    public async Task RefreshSessionsAsync()
    {
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        IReadOnlyList<SessionSummary> summaries;
        try
        {
            (summaries, _elsewhere) = await Task.Run(
                () =>
                {
                    var scanned = Store.Scan(cts.Token);
                    Store.SaveCache();
                    return (scanned, RemoteSessionScanner.RunningSessionIds());
                },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IsLoadingSessions = false;
        _claudeSummaries = summaries;
        MergeSummaries([.. summaries, .. _openCodeSummaries, .. _codexSummaries]);
        _ = RefreshOpenCodeSessionsAsync();
        _ = RefreshCodexSessionsAsync();
        await AutoArchiveSessionsAsync(fold: false);
        OnPropertyChanged(nameof(RecentFolders));
        Current.RefreshFolders();
        _ = LoadGitHubLinksAsync();
        _ = LoadWorktreeMergesAsync();
    }

    // What the last scan of each store found: either one's rescan merges with the other's last list.
    private IReadOnlyList<SessionSummary> _claudeSummaries = [];
    private IReadOnlyList<SessionSummary> _openCodeSummaries = [];
    private IReadOnlyList<SessionSummary> _codexSummaries = [];
    private bool _openCodeScanning;
    private bool _codexScanning;

    /// <summary>
    /// Lists OpenCode's sessions (from its server, started for it) and merges them into the list. Only once OpenCode
    /// has been used here, so no server starts for people who never did.
    /// </summary>
    private async Task RefreshOpenCodeSessionsAsync()
    {
        if (_openCodeScanning || (OpenCodeServer.Current is null && !(OpenCodeServer.HasData && OpenCodeServer.IsInstalled)))
        {
            return;
        }

        _openCodeScanning = true;
        try
        {
            var server = await OpenCodeServer.GetAsync();
            var sessions = await OpenCodeClient.ListSessionsAsync(server);
            _openCodeSummaries = [.. sessions.Select(s => new SessionSummary(
                s.Id, "", s.Directory, s.Title.Length > 0 ? s.Title : UntitledOpenCode, s.UpdatedUtc, 0, Backend: AgentBackends.OpenCode))];
            MergeSummaries([.. _claudeSummaries, .. _openCodeSummaries, .. _codexSummaries]);
            OnPropertyChanged(nameof(RecentFolders));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or FileNotFoundException or TimeoutException or System.Text.Json.JsonException)
        {
            // OpenCode is not there or not answering: its sessions are left out until it is.
        }
        finally
        {
            _openCodeScanning = false;
        }
    }

    private const string UntitledOpenCode = "Untitled OpenCode session";

    /// <summary>
    /// Lists Codex's threads (from its app-server, started for it) and merges them into the list. Only once Codex
    /// has been used here, so no server starts for people who never did.
    /// </summary>
    private async Task RefreshCodexSessionsAsync()
    {
        if (_codexScanning || (Nomad.Core.Codex.CodexServer.Current is null && !(Nomad.Core.Codex.CodexServer.HasData && Nomad.Core.Codex.CodexServer.IsInstalled)))
        {
            return;
        }

        _codexScanning = true;
        try
        {
            var server = await Nomad.Core.Codex.CodexServer.GetAsync();
            var threads = await Nomad.Core.Codex.CodexClient.ListThreadsAsync(server);
            _codexSummaries = [.. threads.Select(t => new SessionSummary(
                t.Id, "", t.Directory, t.Title.Length > 0 ? t.Title : "Untitled Codex session", t.UpdatedUtc, 0, Backend: AgentBackends.Codex))];
            MergeSummaries([.. _claudeSummaries, .. _openCodeSummaries, .. _codexSummaries]);
            OnPropertyChanged(nameof(RecentFolders));
        }
        catch (Exception ex) when (ex is Nomad.Core.Codex.CodexException or InvalidOperationException or IOException or FileNotFoundException or TimeoutException or System.Text.Json.JsonException)
        {
            // Codex is not there or not answering: its sessions are left out until it is.
        }
        finally
        {
            _codexScanning = false;
        }
    }

    /// <summary>The main agent setting changed: the rows' logos follow.</summary>
    public void ApplyMainBackend()
    {
        SessionListItemViewModel.MainBackend = Settings.MainBackend;
        foreach (var item in _all)
        {
            item.RefreshLogo();
        }
    }

    /// <summary>OpenCode's list comes after the transcripts' scan: wait for it when one of these ids is OpenCode's and not listed yet.</summary>
    private async Task AwaitOpenCodeSessionsAsync(IEnumerable<string> ids)
    {
        if (ids.Any(id => id.StartsWith("ses_", StringComparison.Ordinal) && FindItem(id) is null))
        {
            while (_openCodeScanning)
            {
                await Task.Delay(100);
            }

            await RefreshOpenCodeSessionsAsync();
        }

        if (ids.Any(id => FindItem(id) is null))
        {
            while (_codexScanning)
            {
                await Task.Delay(100);
            }

            await RefreshCodexSessionsAsync();
        }
    }

    /// <summary>Why OpenCode lists no models (not installed, failed to start), shown in the model menu; null when fine.</summary>
    [ObservableProperty]
    public partial string? OpenCodeError { get; set; }

    private Task? _openCodeCatalog;

    /// <summary>Stars or unstars an OpenCode model; every open session's chips and shortcuts follow.</summary>
    public void ToggleOpenCodeFavorite(string model)
    {
        var favorites = Settings.OpenCodeFavorites;
        var index = favorites.FindIndex(f => string.Equals(f, model, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            favorites.RemoveAt(index);
        }
        else
        {
            favorites.Add(model);
        }

        SaveSettings();
        foreach (var session in _open.Values.Append(Current).Append(_draft).OfType<SessionViewModel>().Distinct())
        {
            session.OnOpenCodeFavoritesChanged();
        }
    }

    /// <summary>Loads OpenCode's models (starting its server); once per run unless it failed.</summary>
    public Task LoadOpenCodeCatalogAsync()
    {
        if (_openCodeCatalog is { IsCompleted: false } || (_openCodeCatalog is { IsCompletedSuccessfully: true } && OpenCodeError is null))
        {
            return _openCodeCatalog;
        }

        return _openCodeCatalog = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                var server = await OpenCodeServer.GetAsync();
                var (models, fallback) = await OpenCodeClient.ListModelsAsync(server);
                OpenCodeCatalog.Set(models, fallback);
                OpenCodeError = models.Count == 0 ? "OpenCode has no provider connected. Run “opencode auth login” in a terminal." : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or FileNotFoundException or TimeoutException or System.Text.Json.JsonException)
            {
                OpenCodeError = ex.Message;
            }

            foreach (var session in _open.Values.Append(Current).Append(_draft).OfType<SessionViewModel>().Distinct())
            {
                session.OnOpenCodeCatalogChanged();
            }

            _ = RefreshOpenCodeSessionsAsync();
        }
    }

    /// <summary>Why Codex lists no models (not installed, signed out, failed to start), shown in the model menu; null when fine.</summary>
    [ObservableProperty]
    public partial string? CodexError { get; set; }

    private Task? _codexCatalog;

    /// <summary>Loads Codex's models (starting its server); once per run unless it failed.</summary>
    public Task LoadCodexCatalogAsync()
    {
        if (_codexCatalog is { IsCompleted: false } || (_codexCatalog is { IsCompletedSuccessfully: true } && CodexError is null))
        {
            return _codexCatalog;
        }

        return _codexCatalog = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                var server = await Nomad.Core.Codex.CodexServer.GetAsync();
                var models = await Nomad.Core.Codex.CodexClient.ListModelsAsync(server);
                CodexCatalog.Set(models);
                CodexError = models.Count == 0 ? "Codex lists no models. Sign in with “codex login” in a terminal." : null;
            }
            catch (Exception ex) when (ex is Nomad.Core.Codex.CodexException or InvalidOperationException or IOException or FileNotFoundException or TimeoutException or System.Text.Json.JsonException)
            {
                CodexError = ex.Message;
            }

            foreach (var session in _open.Values.Append(Current).Append(_draft).OfType<SessionViewModel>().Distinct())
            {
                session.OnCodexCatalogChanged();
            }

            _ = RefreshCodexSessionsAsync();
        }
    }

    /// <summary>
    /// The first scan hasn't finished: the list shows what the last run saw, or placeholder rows when there is
    /// nothing saved (the first start).
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoadingSessions { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowSessionsSkeleton { get; set; }

    /// <summary>Brings the list's rows in line with these summaries (keeping the rows of sessions still there).</summary>
    private void MergeSummaries(IReadOnlyList<SessionSummary> summaries)
    {
        var byId = _all.ToDictionary(s => s.SessionId, StringComparer.Ordinal);
        var archived = new HashSet<string>(Settings.ArchivedSessions, StringComparer.Ordinal);
        var pinned = new HashSet<string>(Settings.PinnedSessions, StringComparer.Ordinal);
        var merged = new List<SessionListItemViewModel>(summaries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in summaries)
        {
            if (!seen.Add(s.SessionId))
            {
                continue;
            }

            if (!byId.TryGetValue(s.SessionId, out var vm))
            {
                vm = new SessionListItemViewModel(s.SessionId);
            }

            vm.FilePath = s.Backend == AgentBackends.ClaudeCode ? s.FilePath : null;
            vm.Backend = s.Backend;
            vm.WorkingDirectory = Settings.MovedSessionFolders.GetValueOrDefault(s.SessionId) ?? s.WorkingDirectory;
            _open.TryGetValue(s.SessionId, out var live);
            var originalFolder = string.Equals(vm.WorkingDirectory, s.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
            vm.GitBranch = originalFolder ? s.GitBranch : live?.GitBranch;
            vm.PullRequestUrl = originalFolder ? s.PullRequestUrl : null;
            vm.IssueUrl = originalFolder ? s.IssueUrl : null;
            vm.LastActivityUtc = s.LastActivityUtc > vm.LastActivityUtc ? s.LastActivityUtc : vm.LastActivityUtc;
            // An open session knows its newest title (a generated one may not be on disk yet).
            vm.Title = !string.IsNullOrEmpty(live?.Title) ? live.Title : s.Title;
            vm.IsPhone = IsPhone(s.SessionId);
            vm.IsElsewhere = IsRunningElsewhere(s.SessionId);
            vm.IsAlive = live?.IsProcessRunning == true || vm.IsPhone || vm.IsElsewhere;
            if (live is not null)
            {
                // A row made anew for an open session takes its mark from it.
                vm.IsRunning = live.IsTurnRunning;
                vm.IsWaiting = live.PendingPermission is not null;
                vm.HasRunningSubagents = live.HasRunningSubagents;
                vm.HasRunningWorkflows = live.HasRunningWorkflows;
            }

            vm.IsArchived = archived.Contains(s.SessionId);
            vm.IsPinned = pinned.Contains(s.SessionId);
            merged.Add(vm);
        }

        // Live sessions whose transcript is not on disk yet stay in the list.
        foreach (var vm in _all)
        {
            if (!seen.Contains(vm.SessionId) && _open.ContainsKey(vm.SessionId))
            {
                merged.Add(vm);
            }
        }

        merged.Sort((a, b) => b.LastActivityUtc.CompareTo(a.LastActivityUtc));
        _all.Clear();
        _all.AddRange(merged);
        foreach (var vm in _all)
        {
            vm.IsSelected = Current.SessionId is not null && vm.SessionId == Current.SessionId;
        }

        ApplyFilter();
    }

    /// <summary>Sessions in a worktree learn whether its branch has been merged into the repository (local git only).</summary>
    private async Task LoadWorktreeMergesAsync()
    {
        var rows = _all.Where(s => s.WorkingDirectory is { } w && Nomad.Core.Git.GitInfo.WorktreeOwner(w) is not null)
            .Take(GitHubLinkSessions)
            .ToList();
        var lookups = rows.GroupBy(s => Nomad.Core.Git.GitInfo.WorktreeOwner(s.WorkingDirectory!)!, StringComparer.OrdinalIgnoreCase)
            .Select(repo => (repo, Nomad.Core.Git.GitInfo.MergedBranchesAsync(repo.Key)))
            .ToList();
        foreach (var (repo, lookup) in lookups)
        {
            var merged = await lookup;
            foreach (var row in repo)
            {
                // The branch its transcript recorded, else the one a worktree of that name was made on.
                var branch = !string.IsNullOrEmpty(row.GitBranch) && row.GitBranch != "HEAD" ? row.GitBranch
                    : "worktree-" + WorktreeName(row.WorkingDirectory!);
                row.IsWorktreeMerged = merged?.Invoke(branch) == true;
                SleepMergedSession(row);
            }
        }
    }

    /// <summary>Sessions already seen with their worktree merged: each is put to sleep once, so waking it again sticks.</summary>
    private readonly HashSet<string> _mergeSeen = new(StringComparer.Ordinal);

    /// <summary>A session alive in a worktree whose branch has just been merged moves to Idle (once its turn is over).</summary>
    private void SleepMergedSession(SessionListItemViewModel row)
    {
        if (!row.IsWorktreeMerged || _mergeSeen.Contains(row.SessionId))
        {
            return;
        }

        if (_open.TryGetValue(row.SessionId, out var session) && session.IsProcessRunning)
        {
            // Remote Control sessions stay up for the phone; a running turn or background work is waited out (the next
            // check tries again).
            if (session.IsTurnRunning || session.HasRemoteControlUrl || session.HasBackgroundWork)
            {
                return;
            }

            _ = session.SleepAsync();
        }

        _mergeSeen.Add(row.SessionId);
    }

    /// <summary>The worktree's own folder name (<c>…\.claude\worktrees\&lt;name&gt;</c>), from any folder below it.</summary>
    private static string WorktreeName(string folder)
    {
        var owner = Nomad.Core.Git.GitInfo.WorktreeOwner(folder)!;
        var rest = folder[owner.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return rest.Length > 2 ? rest[2] : "";
    }

    private const int GitHubLinkSessions = 60;

    /// <summary>
    /// The newest sessions on a branch get their GitHub pull request and issue (gh, cached per repository for a few
    /// minutes): an icon each on their rows.
    /// </summary>
    private async Task LoadGitHubLinksAsync(SessionListItemViewModel? only = null)
    {
        var rows = (only is null ? _all : [only])
            .Where(s => s.WorkingDirectory is not null && !string.IsNullOrEmpty(s.GitBranch) && s.GitBranch != "HEAD")
            .Take(GitHubLinkSessions)
            .ToList();
        var lookups = rows.GroupBy(s => s.WorkingDirectory!, StringComparer.OrdinalIgnoreCase)
            .Select(folder => (folder, Nomad.Core.Git.GitHubLinks.GetAsync(folder.Key)))
            .ToList();
        foreach (var (folder, lookup) in lookups)
        {
            if (await lookup is not { } links)
            {
                continue;
            }

            foreach (var row in folder)
            {
                // The pull request from its branch, else the last one it mentions (one it opened, or was asked about).
                var pr = links.PullRequestFor(row.GitBranch);
                if (pr is null && row.PullRequestUrl is { } prUrl && links.Trusts(prUrl))
                {
                    pr = links.PullRequests.FirstOrDefault(p => string.Equals(p.Url, prUrl, StringComparison.OrdinalIgnoreCase))
                        ?? await Nomad.Core.Git.GitHubLinks.PullRequestAsync(prUrl, folder.Key);
                }

                // The issue its pull request closes, else the last one it mentions, else one in its branch or title.
                var ownRepo = pr?.Url.Contains($"/{links.NameWithOwner}/", StringComparison.OrdinalIgnoreCase) == true;
                var issue = ownRepo && pr is { Closes.Count: > 0 } ? links.IssueFor(null, null, pr) : null;
                if (issue is null && row.IssueUrl is { } issueUrl && links.Trusts(issueUrl))
                {
                    issue = links.Issues.FirstOrDefault(i => string.Equals(i.Url, issueUrl, StringComparison.OrdinalIgnoreCase))
                        ?? await Nomad.Core.Git.GitHubLinks.IssueAsync(issueUrl, folder.Key);
                }

                row.PullRequest = pr;
                row.Issue = issue ?? links.IssueFor(row.GitBranch, row.Title, null);
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex PullRequestUrl = new(
        @"https://github\.com/[\w.-]+/[\w.-]+/pull/\d+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A pull request a live session has just mentioned (one it opened, say) comes onto its row at once, its icon
    /// fading in, rather than at the next rescan.
    /// </summary>
    public void OnGitHubLinksMentioned(SessionViewModel session, string text)
    {
        if (!text.Contains("github.com/", StringComparison.Ordinal)
            || PullRequestUrl.Matches(text).LastOrDefault()?.Value is not { } url
            || session.SessionId is not { } id
            || _all.FirstOrDefault(s => s.SessionId == id) is not { } row
            || (row.HasPullRequest && string.Equals(row.PullRequestUrl, url, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        row.PullRequestUrl = url;
        _ = LoadGitHubLinksAsync(row);
    }

    /// <summary>Every listed session, newest first (archived ones too): what the session search looks through.</summary>
    public IReadOnlyList<SessionListItemViewModel> SearchableSessions() => _all;

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        IEnumerable<SessionListItemViewModel> items = _all;
        if (query.Length > 0)
        {
            items = _all.Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (s.WorkingDirectory?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Archived sessions stay hidden unless asked for, or searched for.
        var list = items.Where(s => !s.IsArchived || Settings.ShowArchived || query.Length > 0).ToList();
        var alive = list.Where(s => s.IsAlive).ToList();
        foreach (var item in _all)
        {
            item.ShowProject = item.IsAlive && Settings.AliveShowProject;
            if (item.ShowProject)
            {
                item.ProjectName = FolderGroup(item);
            }
        }

        SyncList(Alive, Group(alive, [], Settings.AliveGrouping, "Alive:"));
        SyncList(Recents, Group(list.Where(s => !s.IsAlive && !s.IsArchived).ToList(), list.Where(s => !s.IsAlive && s.IsArchived).ToList(), Settings.SessionGrouping, ""));
        HasAlive = Alive.Count > 0;
        AliveCount = alive.Count;
        HasNoSessions = _all.Count == 0 && !IsLoadingSessions;
        ShowSessionsSkeleton = _all.Count == 0 && IsLoadingSessions;
        NumberRows();
    }

    // Reused across filters, so a heading that stays keeps its row in the list.
    private readonly Dictionary<string, SessionGroupHeader> _headers = new(StringComparer.Ordinal);

    private SessionGroupHeader Header(string key, string title)
    {
        if (!_headers.TryGetValue(key, out var header))
        {
            _headers[key] = header = new SessionGroupHeader(key, title);
        }

        header.IsCollapsed = Settings.CollapsedGroups.Contains(key);
        header.CanStartNew = false;
        header.CanPin = false;
        header.IsFolderPinned = false;
        header.Folder = null;
        return header;
    }

    /// <summary>A session's project, as the Folder grouping and the Alive rows' second line name it.</summary>
    private static string FolderGroup(SessionListItemViewModel s) =>
        s.WorkingDirectory is null || IsScratch(s.WorkingDirectory) ? "No folder" : ProjectName(s.WorkingDirectory);

    /// <summary>
    /// A list's entries (Alive or Idle): the pinned sessions, then the rest (newest first) under their group headings,
    /// then the archived ones. A collapsed group lists only its heading, except while searching.
    /// <paramref name="prefix"/> keeps the lists' remembered collapsed groups apart.
    /// </summary>
    private List<object> Group(List<SessionListItemViewModel> sessions, List<SessionListItemViewModel> archived, string grouping, string prefix)
    {
        var searching = SearchText.Trim().Length > 0;
        var result = new List<object>(sessions.Count + archived.Count + 8);
        void AddGroup(SessionGroupHeader header, IReadOnlyCollection<SessionListItemViewModel> rows)
        {
            header.Count = rows.Count;
            result.Add(header);
            if (!header.IsCollapsed || searching)
            {
                result.AddRange(rows);
            }
        }

        // Without grouping the pinned rows just come first (their pin marks them); otherwise under a heading of their own.
        var pinned = sessions.Where(s => s.IsPinned).ToList();
        sessions = sessions.Where(s => !s.IsPinned).ToList();
        if (grouping == "None")
        {
            result.AddRange(pinned);
            result.AddRange(sessions);
        }
        else
        {
            Func<SessionListItemViewModel, string> key = grouping switch
            {
                "Folder" => FolderGroup,
                "Recent" => s => RecentGroup(s.LastActivityUtc, DateTime.UtcNow),
                _ => s => DateGroup(s.LastActivityUtc),
            };

            if (pinned.Count > 0)
            {
                AddGroup(Header(prefix + "Pinned", "Pinned"), pinned);
            }

            // Groups in the order of their newest session; within a group, newest first. Grouped by folder, the
            // Alive list starts with its pinned folders (in pin order), shown even with no session in them.
            var groups = sessions.GroupBy(key).ToList();
            var alive = prefix == "Alive:";
            if (alive && grouping == "Folder")
            {
                foreach (var folder in Settings.AliveFolders)
                {
                    var name = ProjectName(folder);
                    var rows = groups.FirstOrDefault(g => g.Key == name)?.ToList() ?? [];
                    groups.RemoveAll(g => g.Key == name);
                    if (rows.Count == 0 && searching)
                    {
                        continue;
                    }

                    var header = Header(prefix + grouping + ":" + name, name);
                    header.Folder = folder;
                    header.CanStartNew = header.CanPin = header.IsFolderPinned = true;
                    AddGroup(header, rows);
                }
            }

            foreach (var group in groups)
            {
                var header = Header(prefix + grouping + ":" + group.Key, group.Key);
                if (grouping == "Folder" && group.First().WorkingDirectory is { } folder && !IsScratch(folder))
                {
                    header.Folder = GitInfo.WorktreeOwner(folder) ?? folder;
                    header.CanPin = true;
                }

                header.CanStartNew = alive && grouping == "Folder";
                AddGroup(header, group.ToList());
            }
        }

        if (archived.Count > 0)
        {
            AddGroup(Header(prefix + "Archived", "Archived"), archived);
        }

        return result;
    }

    /// <summary>Folds a group's rows away under its heading, or unfolds them (their height grows back from 0).</summary>
    public async Task ToggleGroupAsync(SessionGroupHeader header)
    {
        var entries = Alive.Contains(header) ? Alive : Recents;
        var index = entries.IndexOf(header);
        var rows = entries.Skip(index + 1).TakeWhile(o => o is SessionListItemViewModel).Cast<SessionListItemViewModel>().ToList();
        if (!header.IsCollapsed)
        {
            header.IsCollapsed = true;
            Settings.CollapsedGroups.Add(header.Key);
            SaveSettings();
            if (SearchText.Trim().Length > 0)
            {
                return; // A search lists every match: the group folds once it ends.
            }

            foreach (var row in rows)
            {
                row.IsLeaving = true;
            }

            await Task.Delay(200);
            ApplyFilter();
            foreach (var row in rows)
            {
                row.IsLeaving = false;
            }

            return;
        }

        header.IsCollapsed = false;
        Settings.CollapsedGroups.Remove(header.Key);
        SaveSettings();

        // The rows come in folded, then unfold once laid out.
        var before = entries.OfType<SessionListItemViewModel>().ToHashSet();
        var all = _all.Where(s => !before.Contains(s)).ToList();
        foreach (var row in all)
        {
            row.IsLeaving = true;
        }

        ApplyFilter();
        await Task.Delay(30);
        foreach (var row in all)
        {
            row.IsLeaving = false;
        }
    }

    /// <summary>The Alive list is folded to its heading.</summary>
    public bool IsAliveCollapsed
    {
        get => Settings.AliveCollapsed;
        set
        {
            if (Settings.AliveCollapsed != value)
            {
                Settings.AliveCollapsed = value;
                NumberRows();
                SaveSettings();
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void ToggleAliveSection() => IsAliveCollapsed = !IsAliveCollapsed;

    /// <summary>The Idle list is folded to its heading.</summary>
    public bool IsIdleCollapsed
    {
        get => Settings.IdleCollapsed;
        set
        {
            if (Settings.IdleCollapsed != value)
            {
                Settings.IdleCollapsed = value;
                NumberRows();
                SaveSettings();
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void ToggleIdleSection() => IsIdleCollapsed = !IsIdleCollapsed;

    // ------------------------------------------------------------------ Ctrl+1…9

    /// <summary>Numbers the rows the sidebar shows, top to bottom (Alive, then Idle), 1 to 9.</summary>
    private void NumberRows()
    {
        var shown = ShownRows().Take(9).ToList();
        foreach (var item in _all)
        {
            item.ShortcutNumber = shown.IndexOf(item) + 1;
        }
    }

    /// <summary>The rows the sidebar shows, top to bottom (Alive, then Idle).</summary>
    private List<SessionListItemViewModel> ShownRows() =>
        (IsAliveCollapsed ? [] : Alive.OfType<SessionListItemViewModel>())
            .Concat(IsIdleCollapsed ? [] : Recents.OfType<SessionListItemViewModel>())
            .ToList();

    // ------------------------------------------------------------------ picking several rows

    // Where Shift+click picks from: the last row Ctrl+clicked (else the open session's).
    private SessionListItemViewModel? _pickAnchor;

    /// <summary>Ctrl+click: picks the row or lets it go. The first pick takes the open session's row along.</summary>
    public void TogglePick(SessionListItemViewModel item)
    {
        if (!_all.Any(s => s.IsPicked) && CurrentRow is { } open && !ReferenceEquals(open, item) && ShownRows().Contains(open))
        {
            open.IsPicked = true;
        }

        item.IsPicked = !item.IsPicked;
        _pickAnchor = item;
    }

    /// <summary>Shift+click: picks the shown rows from the anchor to this one, and only those.</summary>
    public void PickRange(SessionListItemViewModel item)
    {
        var rows = ShownRows();
        var to = rows.IndexOf(item);
        if (to < 0)
        {
            return;
        }

        var anchor = _pickAnchor is { } a && rows.Contains(a) ? a : CurrentRow is { } open && rows.Contains(open) ? open : item;
        var from = rows.IndexOf(anchor);
        var range = rows.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1).ToHashSet();
        foreach (var row in _all)
        {
            row.IsPicked = range.Contains(row);
        }

        _pickAnchor = anchor;
    }

    /// <summary>Lets every picked row go. False when none was.</summary>
    public bool ClearPicks()
    {
        _pickAnchor = null;
        var any = false;
        foreach (var row in _all.Where(s => s.IsPicked))
        {
            row.IsPicked = false;
            any = true;
        }

        return any;
    }

    /// <summary>
    /// What a row's context menu acts on: every shown picked row when it is one of them, else the row alone
    /// (and the picks go, as a right-click elsewhere in Explorer does).
    /// </summary>
    public List<SessionListItemViewModel> MenuTargets(SessionListItemViewModel item)
    {
        if (item.IsPicked)
        {
            return ShownRows().Where(s => s.IsPicked).ToList();
        }

        ClearPicks();
        return [item];
    }

    /// <summary>Makes the rows alive or idle, the ones not so already.</summary>
    public async Task SetAliveAsync(IReadOnlyList<SessionListItemViewModel> items, bool alive)
    {
        ClearPicks();
        foreach (var item in items.Where(s => s.IsAlive != alive).ToList())
        {
            await ToggleAliveAsync(item);
        }
    }

    /// <summary>Ctrl is held (or let go): rows show their Ctrl+number.</summary>
    public void ShowShortcutHints(bool show)
    {
        foreach (var item in _all)
        {
            item.ShortcutHint = show;
        }
    }

    /// <summary>Ctrl+<paramref name="number"/>: opens the row with that number. False when there is none.</summary>
    public bool OpenByShortcut(int number)
    {
        if (_all.FirstOrDefault(s => s.ShortcutNumber == number) is not { } item)
        {
            return false;
        }

        _ = OpenSessionAsync(item);
        return true;
    }

    /// <summary>"Last 2 hours", "Last 8 hours", "Last 24 hours", then each day before ("26.10.2026").</summary>
    public static string RecentGroup(DateTime utc, DateTime nowUtc) => (nowUtc - utc).TotalHours switch
    {
        < 2 => "Last 2 hours",
        < 8 => "Last 8 hours",
        < 24 => "Last 24 hours",
        _ => utc.ToLocalTime().ToString("d.M.yyyy", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>"Today", "Yesterday", "Previous 7 days", "Previous 30 days", then the month ("September 2026").</summary>
    public static string DateGroup(DateTime utc)
    {
        var today = DateTime.Today;
        var day = utc.ToLocalTime().Date;
        return (today - day).TotalDays switch
        {
            <= 0 => "Today",
            <= 1 => "Yesterday",
            <= 7 => "Previous 7 days",
            <= 30 => "Previous 30 days",
            _ => day.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    // ------------------------------------------------------------------ pin

    /// <summary>
    /// Pins a session (to the top of its list, kept alive and out of auto-archive) or unpins it. Pinning an archived
    /// one brings it back. The row folds away and unfolds in its new place.
    /// </summary>
    public async Task TogglePinnedAsync(SessionListItemViewModel item)
    {
        await FoldAwayAsync([item]);
        var pin = !item.IsPinned;
        Settings.PinnedSessions.Remove(item.SessionId);
        if (pin)
        {
            Settings.PinnedSessions.Add(item.SessionId);
            if (item.IsArchived)
            {
                Unarchive(item);
            }
        }

        SaveSettings();
        item.IsPinned = pin;
        ApplyFilter();
        await Task.Delay(30);
        item.IsLeaving = false;
    }

    // ------------------------------------------------------------------ archive and delete

    /// <summary>Brings a session back; that counts as touching it, so auto-archive waits its full time again.</summary>
    private void Unarchive(SessionListItemViewModel item)
    {
        Settings.ArchivedSessions.Remove(item.SessionId);
        Settings.UnarchivedUtc[item.SessionId] = DateTime.UtcNow;
        SaveSettings();
        item.IsArchived = false;
    }

    /// <summary>The auto-archive time, at least an hour.</summary>
    public TimeSpan AutoArchiveAfter => TimeSpan.FromHours(Math.Max(1, Settings.AutoArchiveHours));

    /// <summary>The auto-archive settings changed: archive what is due now.</summary>
    public void ApplyAutoArchive() => _ = AutoArchiveSessionsAsync(fold: true);

    /// <summary>"Close to tray" was switched: the window shows or hides its tray icon.</summary>
    public event Action? CloseToTrayChanged;

    public void OnCloseToTrayChanged() => CloseToTrayChanged?.Invoke();

    /// <summary>
    /// Archives the sessions untouched for <see cref="AutoArchiveAfter"/>: not the open one, nor one that is alive,
    /// running a turn, pinned, or run by the phone or elsewhere. Listed rows fold away first when <paramref name="fold"/>.
    /// </summary>
    private async Task AutoArchiveSessionsAsync(bool fold)
    {
        if (!Settings.AutoArchive)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - AutoArchiveAfter;
        var due = _all.Where(item =>
        {
            var touched = Settings.UnarchivedUtc.TryGetValue(item.SessionId, out var unarchived) && unarchived > item.LastActivityUtc
                ? unarchived
                : item.LastActivityUtc;
            return !item.IsArchived && !item.IsPinned && touched < cutoff && !item.IsAlive && !item.IsRunning && item.CanManage
                && item.SessionId != Current.SessionId && !(_open.TryGetValue(item.SessionId, out var s) && s.IsProcessRunning);
        }).ToList();
        if (due.Count == 0)
        {
            return;
        }

        if (fold && !Settings.ShowArchived)
        {
            foreach (var item in due)
            {
                item.IsLeaving = true;
            }

            await Task.Delay(200);
        }

        foreach (var item in due)
        {
            Settings.ArchivedSessions.Add(item.SessionId);
            Settings.UnarchivedUtc.Remove(item.SessionId);
            item.IsArchived = true;
            item.IsLeaving = false;
        }

        SaveSettings();
        ApplyFilter();
    }

    /// <summary>Archives a session (ending its process) or brings it back.</summary>
    public Task ToggleArchivedAsync(SessionListItemViewModel item) => SetArchivedAsync([item], !item.IsArchived);

    /// <summary>Archives the rows together (ending their processes), or brings them back.</summary>
    public async Task SetArchivedAsync(IReadOnlyList<SessionListItemViewModel> items, bool archive)
    {
        ClearPicks();
        if (!archive)
        {
            foreach (var item in items.Where(s => s.IsArchived))
            {
                Unarchive(item);
            }

            ApplyFilter();
            return;
        }

        // Not the ones whose process belongs to the phone or elsewhere.
        var due = items.Where(s => !s.IsArchived && s.CanManage).ToList();
        if (due.Count == 0)
        {
            return;
        }

        if (!Settings.ShowArchived)
        {
            await FoldAwayAsync(due);
        }

        foreach (var item in due)
        {
            Settings.ArchivedSessions.Remove(item.SessionId);
            Settings.ArchivedSessions.Add(item.SessionId);
            Settings.UnarchivedUtc.Remove(item.SessionId);
            item.IsArchived = true;
            item.IsLeaving = false;
        }

        SaveSettings();
        await Task.WhenAll(due
            .Select(item => _open.TryGetValue(item.SessionId, out var session) && session.IsProcessRunning ? session.SleepAsync() : Task.CompletedTask)
            .ToList());
        ApplyFilter();
    }

    public Task DeleteSessionAsync(SessionListItemViewModel item) => DeleteSessionsAsync([item]);

    /// <summary>Deletes the rows together: they fold away at once, then go one by one.</summary>
    public async Task DeleteSessionsAsync(IReadOnlyList<SessionListItemViewModel> items)
    {
        ClearPicks();
        var due = items.Where(s => s.CanManage).ToList();
        if (due.Count == 0)
        {
            return;
        }

        // Its files with unsaved edits would go with it.
        var edited = due.SelectMany(i => _open.TryGetValue(i.SessionId, out var s) ? s.Files.EditedFiles : []);
        if (!await Unsaved.AskAsync(edited, due.Count == 1 ? "delete this chat" : "delete these chats"))
        {
            return;
        }

        await FoldAwayAsync(due);
        foreach (var item in due)
        {
            await DeleteFoldedAsync(item);
            Hub.OnDeleted(item.Backend, item.SessionId);
        }

        ApplyFilter();
    }

    /// <summary>Ends a session's process and moves its transcript (and its folder of tool results) to the Recycle Bin.</summary>
    private async Task DeleteFoldedAsync(SessionListItemViewModel item)
    {
        if (_open.Remove(item.SessionId, out var session))
        {
            await session.ShutdownAsync();
            DropFromHistory(session);
            if (ReferenceEquals(session, Current))
            {
                _navigating = true;
                try
                {
                    Current = _draft is { IsNew: true } draft ? draft : new SessionViewModel(this, Settings.LastFolder);
                }
                finally
                {
                    _navigating = false;
                }
            }
        }

        if (item.IsOpenCode)
        {
            try
            {
                await OpenCodeClient.DeleteSessionAsync(await OpenCodeServer.GetAsync(), item.WorkingDirectory, item.SessionId);
                _openCodeSummaries = [.. _openCodeSummaries.Where(s => s.SessionId != item.SessionId)];
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
            {
                item.IsLeaving = false;
                return;
            }
        }
        else if (item.IsCodex)
        {
            // Into Codex's own archive rather than gone, as a transcript goes to the Recycle Bin.
            try
            {
                await Nomad.Core.Codex.CodexClient.ArchiveAsync(await Nomad.Core.Codex.CodexServer.GetAsync(), item.SessionId);
                _codexSummaries = [.. _codexSummaries.Where(s => s.SessionId != item.SessionId)];
            }
            catch (Exception ex) when (ex is Nomad.Core.Codex.CodexException or InvalidOperationException or IOException or TimeoutException)
            {
                item.IsLeaving = false;
                return;
            }
        }

        var file = !item.IsClaudeCode ? null : item.FilePath ?? Store.FindTranscript(item.SessionId);
        if (file is not null)
        {
            var folder = Path.Combine(Path.GetDirectoryName(file)!, item.SessionId);
            var deleted = await Task.Run(() => RecycleBin.Delete([file, folder]));
            if (!deleted && File.Exists(file))
            {
                // The shell said no (a file in use): the row comes back.
                item.IsLeaving = false;
                return;
            }
        }

        _all.Remove(item);
        if (Settings.ArchivedSessions.Remove(item.SessionId) | Settings.MovedSessionFolders.Remove(item.SessionId))
        {
            SaveSettings();
        }
    }

    /// <summary>Lets the rows fold their height and fade (the row style's transitions) before they leave the list.</summary>
    private static Task FoldAwayAsync(SessionListItemViewModel item) => FoldAwayAsync([item]);

    private static async Task FoldAwayAsync(IEnumerable<SessionListItemViewModel> items)
    {
        foreach (var item in items)
        {
            item.IsLeaving = true;
        }

        await Task.Delay(200);
    }

    private void DropFromHistory(SessionViewModel session)
    {
        foreach (var stack in new[] { _back, _forward })
        {
            var kept = stack.Where(s => !ReferenceEquals(s, session)).Reverse().ToList();
            stack.Clear();
            foreach (var s in kept)
            {
                stack.Push(s);
            }
        }

        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    internal static void SyncList<T>(ObservableCollection<T> target, List<T> list)
        where T : class
    {
        // Minimal-diff update keeps scroll position and avoids rebuilding every row.
        for (var i = 0; i < list.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], list[i]))
            {
                continue;
            }

            var existing = target.IndexOf(list[i]);
            if (existing > i)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, list[i]);
            }
        }

        while (target.Count > list.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    // ------------------------------------------------------------------ callbacks from sessions

    public void OnSessionIdentified(SessionViewModel session)
    {
        if (session.SessionId is not { } id)
        {
            return;
        }

        _open[id] = session;
        var vm = _all.FirstOrDefault(s => s.SessionId == id);
        if (vm is null)
        {
            vm = new SessionListItemViewModel(id) { Title = session.Title, WorkingDirectory = session.Folder };
            _all.Insert(0, vm);
        }

        vm.LastActivityUtc = DateTime.UtcNow;
        vm.IsRunning = session.IsTurnRunning;
        vm.IsWaiting = session.PendingPermission is not null;
        vm.HasRunningSubagents = session.HasRunningSubagents;
        vm.HasRunningWorkflows = session.HasRunningWorkflows;
        vm.IsAlive = session.IsProcessRunning;
        vm.IsSelected = ReferenceEquals(session, Current);
        MoveToTop(vm);
    }

    public void OnAliveChanged(SessionViewModel session)
    {
        HubAliveChanged(session);
        if (session.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } vm && vm.IsAlive != (session.IsProcessRunning || IsRunningElsewhere(id)))
        {
            vm.IsAlive = session.IsProcessRunning || IsRunningElsewhere(id);
            vm.IsElsewhere = IsRunningElsewhere(id);
            ApplyFilter();
        }
    }

    /// <summary>Opens a sidebar row's title for editing (any other row being renamed is saved first).</summary>
    public void BeginRename(SessionListItemViewModel item)
    {
        foreach (var other in _all.Where(s => s.IsRenaming && !ReferenceEquals(s, item)).ToList())
        {
            EndRename(other, save: true);
        }

        item.RenameText = item.Title;
        item.IsRenaming = true;
    }

    /// <summary>Saves (as Claude Code's <c>/rename</c> does) or drops a rename. An empty or unchanged name keeps the title.</summary>
    public void EndRename(SessionListItemViewModel item, bool save)
    {
        if (!item.IsRenaming)
        {
            return;
        }

        item.IsRenaming = false;
        var title = item.RenameText.ReplaceLineEndings(" ").Trim();
        if (!save || title.Length == 0 || title == item.Title)
        {
            return;
        }

        if (item.IsOpenCode)
        {
            _ = RenameOpenCodeAsync(item.WorkingDirectory, item.SessionId, title);
        }
        else if (item.IsCodex)
        {
            _ = RenameCodexAsync(item.SessionId, title);
        }
        else
        {
            var file = item.FilePath ?? Store.FindTranscript(item.SessionId);
            if (file is null)
            {
                return;
            }

            try
            {
                SessionStore.AppendCustomTitle(file, item.SessionId, title);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        item.Title = SessionTitles.Shorten(title);
        if (_open.Values.Append(Current).FirstOrDefault(s => s.SessionId == item.SessionId) is { } session)
        {
            session.Title = item.Title;
            session.RenamedTitle = title;
        }
    }

    private static async Task RenameOpenCodeAsync(string? directory, string sessionId, string title)
    {
        try
        {
            await OpenCodeClient.UpdateSessionAsync(await OpenCodeServer.GetAsync(), directory ?? "", sessionId, title, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            // The sidebar shows the name until the next scan brings OpenCode's back.
        }
    }

    private static async Task RenameCodexAsync(string threadId, string title)
    {
        try
        {
            await Nomad.Core.Codex.CodexClient.SetNameAsync(await Nomad.Core.Codex.CodexServer.GetAsync(), threadId, title);
        }
        catch (Exception ex) when (ex is Nomad.Core.Codex.CodexException or InvalidOperationException or IOException or TimeoutException)
        {
            // The sidebar shows the name until the next scan brings Codex's back.
        }
    }

    /// <summary>A renamed session's CLI has exited: its name goes back on top of the title the CLI re-appended.</summary>
    public void ReassertTitle(SessionViewModel session)
    {
        if (session is { RenamedTitle: { } title, SessionId: { } id } && Store.FindTranscript(id) is { } file)
        {
            try
            {
                SessionStore.AppendCustomTitle(file, id, title);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The sidebar keeps showing the name until the next scan.
            }
        }
    }

    /// <summary>The title bar's title is being edited (a click on it), saved as a rename of the current session's row.</summary>
    [ObservableProperty]
    public partial bool IsTitleRenaming { get; set; }

    [ObservableProperty]
    public partial string TitleRenameText { get; set; } = "";

    /// <summary>Starts editing the title bar's title; false when the current session has no row to rename yet.</summary>
    public bool BeginTitleRename()
    {
        if (CurrentRow is not { } row)
        {
            return false;
        }

        if (row.IsRenaming)
        {
            EndRename(row, save: true);
        }

        TitleRenameText = Current.RenamedTitle ?? (Current.Title.Length > 0 ? Current.Title : row.Title);
        IsTitleRenaming = true;
        return true;
    }

    public void EndTitleRename(bool save)
    {
        if (!IsTitleRenaming)
        {
            return;
        }

        IsTitleRenaming = false;
        if (save && CurrentRow is { } row)
        {
            row.RenameText = TitleRenameText;
            row.IsRenaming = true;
            EndRename(row, save: true);
        }
    }

    /// <summary>F2: rename the current session's row.</summary>
    public SessionListItemViewModel? CurrentRow =>
        Current.SessionId is { } id ? _all.FirstOrDefault(s => s.SessionId == id) : null;

    public void OnSessionTitleChanged(SessionViewModel session)
    {
        if (session.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } vm)
        {
            vm.Title = session.Title;
        }
    }

    public void OnSessionActivity(SessionViewModel session)
    {
        HubActivity(session);
        if (session.Folder is not null)
        {
            RememberFolder(session.Folder);
        }

        if (session.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } vm)
        {
            vm.IsRunning = session.IsTurnRunning;
            vm.LastActivityUtc = DateTime.UtcNow;
            if (vm.IsArchived && session.IsTurnRunning)
            {
                Unarchive(vm); // A prompt sent to an archived session brings it back.
            }

            MoveToTop(vm);
        }
    }

    public void OnSessionFolderChanged(SessionViewModel session)
    {
        if (session.SessionId is not { } id || session.Folder is not { } folder)
        {
            return;
        }

        Settings.MovedSessionFolders[id] = folder;
        if (_all.FirstOrDefault(s => s.SessionId == id) is { } row)
        {
            row.WorkingDirectory = folder;
            row.GitBranch = null;
            row.PullRequestUrl = null;
            row.IssueUrl = null;
            row.PullRequest = null;
            row.Issue = null;
        }

        RememberFolder(folder);
        ApplyFilter();
    }

    public void OnTurnCompleted(SessionViewModel session)
    {
        HubTurnCompleted(session);
        if (session.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } vm)
        {
            vm.IsRunning = false;
            vm.IsUnread = !ReferenceEquals(session, Current);
            vm.LastActivityUtc = DateTime.UtcNow;
        }

        TurnCompleted?.Invoke(session);
        _ = RefreshSessionsAsync();
        _ = session.Usage.RefreshAsync();
    }

    /// <summary>A permission or question is pending, or was answered: the row's yellow dot.</summary>
    public void OnWaitingChanged(SessionViewModel session)
    {
        if (session.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } vm)
        {
            vm.IsWaiting = session.PendingPermission is not null;
        }
    }

    /// <summary>A subagent started or ended: the row's subagent mark.</summary>
    /// <summary>The account may run workflows: the CLI said so (sessions offer the Ultracode switch then).</summary>
    public bool UltracodeAvailable
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            foreach (var session in LiveSessions)
            {
                session.OnUltracodeAvailableChanged();
            }
        }
    }

    /// <summary>The size Claude's workflows aim for, for every session; running ones take it at once.</summary>
    public void SetWorkflowSize(string size)
    {
        Settings.WorkflowSize = size == "medium" ? null : size;
        SaveSettings();
        foreach (var session in LiveSessions)
        {
            _ = session.ApplyWorkflowSizeAsync();
        }
    }

    public void OnSubagentsChanged(SessionViewModel session)
    {
        if (session.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } vm)
        {
            vm.HasRunningSubagents = session.HasRunningSubagents;
            vm.HasRunningWorkflows = session.HasRunningWorkflows;
        }
    }

    public void OnPermissionRequested(SessionViewModel session)
    {
        PermissionRequested?.Invoke(session);
        if (!ReferenceEquals(session, Current) && session.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } vm)
        {
            vm.IsUnread = true;
        }
    }

    public void OnAccountInfo(AccountInfoEvent info)
    {
        var plan = info.SubscriptionType switch
        {
            null or "" => null,
            var s when s.Contains("max", StringComparison.OrdinalIgnoreCase) => "Max",
            var s when s.Contains("pro", StringComparison.OrdinalIgnoreCase) => "Pro",
            var s when s.Contains("team", StringComparison.OrdinalIgnoreCase) => "Team",
            var s when s.Contains("enterprise", StringComparison.OrdinalIgnoreCase) => "Enterprise",
            var s => s,
        };

        if (plan is not null && plan != PlanLabel)
        {
            PlanLabel = plan;
            Settings.LastPlan = plan;
            SaveSettings();
        }
    }

    /// <summary>Raised when any session finished a turn (dev screenshot hook).</summary>
    public event Action<SessionViewModel>? TurnCompleted;

    /// <summary>Raised when a session asks for a permission (dev hook <c>--dev-answer</c>).</summary>
    public event Action<SessionViewModel>? PermissionRequested;

    private void MoveToTop(SessionListItemViewModel vm)
    {
        var index = _all.IndexOf(vm);
        if (index > 0)
        {
            _all.RemoveAt(index);
            _all.Insert(0, vm);
        }

        ApplyFilter();
    }

    // ------------------------------------------------------------------ folders and settings

    public void RememberFolder(string folder)
    {
        folder = GitInfo.WorktreeOwner(folder) ?? folder;
        if (IsScratch(folder))
        {
            return;
        }

        Settings.TouchRecentFolder(folder);
        Settings.LastFolder = folder;
        SaveSettings();
        OnPropertyChanged(nameof(RecentFolders));
        Current.RefreshFolders();
    }

    /// <summary>
    /// Folders of "No folder" sessions. Not under %LOCALAPPDATA%\Nomad: that is the Setup install's folder, which
    /// Velopack replaces on install and deletes on uninstall.
    /// </summary>
    public static string ScratchRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NomadData", "scratch");

    // Where earlier builds kept them (before Setup installs owned %LOCALAPPDATA%\Nomad): not recent folders either.
    private static readonly string[] LegacyScratchRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nomad", "scratch"),
    ];

    private static bool IsScratch(string folder) =>
        folder.StartsWith(ScratchRoot, StringComparison.OrdinalIgnoreCase) ||
        LegacyScratchRoots.Any(root => folder.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    /// <summary>"No folder": Claude works in a fresh scratch folder.</summary>
    public string CreateScratchFolder()
    {
        var path = Path.Combine(ScratchRoot, DateTime.Now.ToString("yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(path);
        return path;
    }

    public void SaveSettings()
    {
        // --dev-set tries appearance settings for a test run: they must never land in the user's own file.
        if (Dev.Set.Count > 0 && Dev.SettingsPath is null)
        {
            return;
        }

        try
        {
            SettingsStore.Save(Settings, _settingsPath);
            Hub.OnSettingsSaved();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: settings are kept in memory.
        }
    }

    /// <summary>The sessions that may have a CLI running: every opened one and the current one.</summary>
    private IEnumerable<SessionViewModel> LiveSessions => _open.Values.Append(Current).Distinct();

    /// <summary>
    /// A turn is running here, or a phone session is (Nomad can't see its turns): Nomad must not restart now, because
    /// its CLI processes die with it.
    /// </summary>
    public bool HasRunningTurns => LiveSessions.Any(s => s.IsTurnRunning) || _phone.Count > 0;

    /// <summary>
    /// Nothing a restart would interrupt, even between turns: no turn, no prompt being typed and no session that
    /// claude.ai or the phone is connected to through Remote Control. Only then does an update restart Nomad by itself.
    /// </summary>
    public bool CanRestartQuietly =>
        !HasRunningTurns && !LiveSessions.Any(s => s.HasComposerText || (s.IsProcessRunning && s.HasRemoteControlUrl))
        && _draft is not { HasAttachments: true };

    private bool _shuttingDown;

    /// <summary>The prompt a session whose turn Nomad's exit cut short is resumed with at the next start.</summary>
    public const string InterruptedPrompt = "You were interrupted by an app restart. Continue";

    /// <summary>
    /// Keeps <see cref="NomadSettings.InterruptedSessions"/> in step with the running turns (saved at once, so a crash
    /// leaves it right). Frozen once Nomad shuts down: the turns its exit ends are the ones to resume.
    /// </summary>
    public void OnTurnRunningChanged(SessionViewModel session)
    {
        // Every start and end of a turn, also those the CLI begins itself (a queued prompt right after the last turn,
        // a resumed one), keeps the row's mark in step.
        if (session.SessionId is { } rowId && _all.FirstOrDefault(s => s.SessionId == rowId) is { } row)
        {
            row.IsRunning = session.IsTurnRunning;
        }

        if (_shuttingDown || Dev.IsActive || session.SessionId is not { } id || session.IsPhoneSession)
        {
            return;
        }

        var list = Settings.InterruptedSessions;
        var changed = session.IsTurnRunning ? !list.Contains(id) : list.Remove(id);
        if (changed)
        {
            if (session.IsTurnRunning)
            {
                list.Add(id);
            }

            SaveSettings();
        }
    }

    /// <summary>At startup: the sessions whose turn the last exit interrupted wake in the background and carry on.</summary>
    public async Task ResumeInterruptedAsync()
    {
        if (Dev.IsActive || Settings.InterruptedSessions.Count == 0)
        {
            return;
        }

        var ids = Settings.InterruptedSessions.ToList();
        Settings.InterruptedSessions.Clear();
        SaveSettings();
        await AwaitOpenCodeSessionsAsync(ids);
        foreach (var id in ids)
        {
            var item = FindItem(id);
            if (item is not { IsStored: true } || !item.CanManage || IsRunningElsewhere(id))
            {
                continue;
            }

            if (!_open.TryGetValue(id, out var session))
            {
                session = CreateStoredSession(item);
                await LoadHistoryAsync(session, item);
            }

            if (!session.IsTurnRunning)
            {
                _ = session.SendPromptAsync(InterruptedPrompt);
            }
        }
    }

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _idleTimer.Stop();
        _usageTimer.Stop();
        _phoneTimer.Stop();
        _elsewhereTimer.Stop();
        SaveDraft();
        var sessions = _open.Values.Append(Current).Distinct().ToList();
        await Task.WhenAll(sessions.Select(s => s.ShutdownAsync()).Concat(_servers.Values.Select(s => s.StopAsync())));
    }

    private static string Initials(string name)
    {
        var parts = name.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}",
        };
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
