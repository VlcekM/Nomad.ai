using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klodi.App.Infrastructure;
using Klodi.Core.Agents;
using Klodi.Core.Sessions;
using Klodi.Core.Settings;

namespace Klodi.App.ViewModels;

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

    public MainViewModel(KlodiSettings settings, string settingsPath, DevOptions dev)
    {
        Settings = settings;
        _settingsPath = settingsPath;
        Dev = dev;
        Store = new SessionStore();
        IsSidebarOpen = !settings.SidebarCollapsed;
        PlanLabel = string.IsNullOrEmpty(settings.LastPlan) ? null : settings.LastPlan;

        ApplyDisplayName();

        Current = new SessionViewModel(this, dev.Cwd ?? settings.LastFolder);
        if (dev.Model is not null)
        {
            Current.Model = dev.Model;
        }
    }

    public KlodiSettings Settings { get; }

    public DevOptions Dev { get; }

    public SessionStore Store { get; }

    public ObservableCollection<SessionListItemViewModel> Recents { get; } = [];

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewSelected))]
    public partial SessionViewModel Current { get; set; }

    public bool IsNewSelected => Current.IsNew;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

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
            foreach (var f in Settings.RecentFolders.Concat(_all.Select(s => s.WorkingDirectory).OfType<string>()))
            {
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

    partial void OnIsSidebarOpenChanged(bool value)
    {
        Settings.SidebarCollapsed = !value;
        SaveSettings();
    }

    partial void OnCurrentChanged(SessionViewModel oldValue, SessionViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnCurrentPropertyChanged;

            // Back/forward history; an untouched new session is not worth going back to.
            if (!_navigating && !(oldValue.IsNew && oldValue.Items.Count == 0))
            {
                _back.Push(oldValue);
                _forward.Clear();
            }
        }

        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        newValue.RefreshFolders();

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
        if (e.PropertyName == nameof(SessionViewModel.IsNew))
        {
            OnPropertyChanged(nameof(IsNewSelected));
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

        Current = new SessionViewModel(this, Settings.LastFolder);
    }

    [RelayCommand]
    public async Task OpenSessionAsync(SessionListItemViewModel item)
    {
        if (_open.TryGetValue(item.SessionId, out var live))
        {
            Current = live;
            return;
        }

        if (item.FilePath is null)
        {
            return;
        }

        var session = new SessionViewModel(this, item.WorkingDirectory) { IsLoadingHistory = true, Title = item.Title, IsNew = false, SessionId = item.SessionId };
        _open[item.SessionId] = session;
        Current = session;

        var summary = new SessionSummary(item.SessionId, item.FilePath, item.WorkingDirectory, item.Title, item.LastActivityUtc, 0);
        try
        {
            var history = await Task.Run(() => SessionStore.LoadHistory(item.FilePath));
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

    /// <summary>Opens a stored session by id (dev hook <c>--dev-session</c>).</summary>
    public async Task OpenSessionByIdAsync(string sessionId)
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

        if (item is not null)
        {
            await OpenSessionAsync(item);
        }
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
        SettingsPage = new SettingsViewModel(this);
        IsSettingsOpen = true;
    }

    [RelayCommand]
    public void CloseSettings()
    {
        IsSettingsOpen = false;
        SettingsPage = null;
        SaveSettings();
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
            summaries = await Task.Run(() => Store.Scan(cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var byId = _all.ToDictionary(s => s.SessionId, StringComparer.Ordinal);
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

            vm.FilePath = s.FilePath;
            vm.WorkingDirectory = s.WorkingDirectory;
            vm.LastActivityUtc = s.LastActivityUtc > vm.LastActivityUtc ? s.LastActivityUtc : vm.LastActivityUtc;
            // An open session knows its newest title (a generated one may not be on disk yet).
            vm.Title = _open.TryGetValue(s.SessionId, out var live) && !string.IsNullOrEmpty(live.Title) ? live.Title : s.Title;
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
        OnPropertyChanged(nameof(RecentFolders));
        Current.RefreshFolders();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        IEnumerable<SessionListItemViewModel> items = _all;
        if (query.Length > 0)
        {
            items = _all.Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (s.WorkingDirectory?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = items.ToList();

        // Minimal-diff update keeps scroll position and avoids rebuilding every row.
        for (var i = 0; i < list.Count; i++)
        {
            if (i < Recents.Count && ReferenceEquals(Recents[i], list[i]))
            {
                continue;
            }

            var existing = Recents.IndexOf(list[i]);
            if (existing > i)
            {
                Recents.Move(existing, i);
            }
            else
            {
                Recents.Insert(i, list[i]);
            }
        }

        while (Recents.Count > list.Count)
        {
            Recents.RemoveAt(Recents.Count - 1);
        }

        HasNoSessions = _all.Count == 0;
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
        vm.IsSelected = ReferenceEquals(session, Current);
        MoveToTop(vm);
    }

    public void OnSessionTitleChanged(SessionViewModel session)
    {
        if (session.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } vm)
        {
            vm.Title = session.Title;
        }
    }

    public void OnSessionActivity(SessionViewModel session)
    {
        if (session.Folder is not null)
        {
            RememberFolder(session.Folder);
        }

        if (session.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } vm)
        {
            vm.IsRunning = session.IsTurnRunning;
            vm.LastActivityUtc = DateTime.UtcNow;
            MoveToTop(vm);
        }
    }

    public void OnTurnCompleted(SessionViewModel session)
    {
        if (session.SessionId is { } id && _all.FirstOrDefault(s => s.SessionId == id) is { } vm)
        {
            vm.IsRunning = false;
            vm.IsUnread = !ReferenceEquals(session, Current);
            vm.LastActivityUtc = DateTime.UtcNow;
        }

        TurnCompleted?.Invoke(session);
        _ = RefreshSessionsAsync();
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

    public static string ScratchRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klodi", "scratch");

    private static bool IsScratch(string folder) =>
        folder.StartsWith(ScratchRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>"No folder": Claude works in a fresh scratch folder.</summary>
    public string CreateScratchFolder()
    {
        var path = Path.Combine(ScratchRoot, DateTime.Now.ToString("yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(path);
        return path;
    }

    public void SaveSettings()
    {
        try
        {
            SettingsStore.Save(Settings, _settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: settings are kept in memory.
        }
    }

    public async Task ShutdownAsync()
    {
        var sessions = _open.Values.Append(Current).Distinct().ToList();
        await Task.WhenAll(sessions.Select(s => s.ShutdownAsync()));
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
