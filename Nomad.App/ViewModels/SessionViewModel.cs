using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Infrastructure;
using Nomad.Core.Agents;
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
        Effort = main.Settings.DefaultEffort;
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

    /// <summary>No prompt sent and no history: the new-session view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerPlaceholder))]
    [NotifyPropertyChangedFor(nameof(TitleOrPlaceholder))]
    [NotifyPropertyChangedFor(nameof(HasTitle))]
    public partial bool IsNew { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderName))]
    [NotifyPropertyChangedFor(nameof(HasFolder))]
    [NotifyPropertyChangedFor(nameof(FolderToolTip))]
    public partial string? Folder { get; set; }

    public string FolderName => Format.FolderName(Folder);

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
            o.IsSelected = Folder is not null && string.Equals(o.Id.TrimEnd('\\', '/'), Folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------ composer

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComposerText))]
    [NotifyPropertyChangedFor(nameof(ShowSendButton))]
    public partial string ComposerText { get; set; } = "";

    public bool HasComposerText => !string.IsNullOrWhiteSpace(ComposerText);

    public bool ShowSendButton => HasComposerText && !IsTurnRunning;

    public string ComposerPlaceholder => IsNew ? "Describe a task or ask a question" : "Type / for commands";

    // ------------------------------------------------------------------ turn state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSendButton))]
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
            _workingTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateWorking());
            _workingTimer.Start();
            UpdateWorking();
        }
        else
        {
            _workingTimer?.Stop();
        }
    }

    private void UpdateWorking()
    {
        var elapsed = DateTime.UtcNow - _turnStartUtc;
        WorkingElapsed = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
        WorkingText = Items.LastOrDefault() switch
        {
            ThinkingItem { IsStreaming: true } => "Thinking",
            ToolGroupItem { IsRunning: true, Summary.Length: > 0 } group => group.Summary,
            AssistantTextItem { IsStreaming: true } => "Writing",
            PermissionItem { IsAnswered: false } => "Waiting for you",
            _ => "Working",
        };
    }

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
    public partial double ContextFraction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextToolTip))]
    public partial long ContextTokens { get; set; }

    public string ContextToolTip => ContextTokens > 0
        ? $"Context: {Format.Tokens(ContextTokens)} of {Format.Tokens(Catalog.ContextWindow(Model))} ({ContextFraction:P0})"
        : "Context: empty";

    [ObservableProperty]
    public partial bool IsLoadingHistory { get; set; }

    [ObservableProperty]
    public partial PermissionItem? PendingPermission { get; set; }

    public bool IsProcessRunning => _backend?.IsProcessRunning == true;

    /// <summary>The app's plan usage, for the ring's popup.</summary>
    public UsageViewModel Usage => _main.Usage;

    /// <summary>When the CLI process last started or sent anything; drives the idle timeout.</summary>
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Raised after items were added or grew, so the view can follow the bottom.</summary>
    public event Action? ContentChanged;

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    public async Task SendAsync()
    {
        var text = ComposerText.Trim();
        if (text.Length == 0 || IsTurnRunning || IsPhoneSession)
        {
            return;
        }

        ComposerText = "";
        await SendPromptAsync(text);
    }

    public async Task SendPromptAsync(string text)
    {
        var first = IsNew;
        if (first)
        {
            Title = SessionTitles.Shorten(text);
            IsNew = false;
        }

        CloseGroup();
        var message = new UserMessageItem(text, Guid.NewGuid().ToString(), DateTime.UtcNow, seen: false);
        _unseen = message;
        Items.Add(message);
        IsTurnRunning = true;
        State = AgentState.Running;
        HasRun = true;
        ContentChanged?.Invoke();
        _main.OnSessionActivity(this);

        try
        {
            await EnsureBackendAsync();
            await _backend!.SendPromptAsync(text, message.Uuid);

            // The process now holds the rewind / fork point and the transcript will record it.
            _resumeAt = null;
            _forkFrom = null;
            if (first)
            {
                _ = GenerateTitleAsync(_backend, text);
            }

            // A new session gets its bridge once the prompt gave it a name.
            _ = SyncRemoteControlAsync();
        }
        catch (Exception ex)
        {
            IsTurnRunning = false;
            State = AgentState.Error;
            Items.Add(new ErrorItem(ex.Message));
            ContentChanged?.Invoke();
            _main.OnSessionActivity(this);
        }
    }

    /// <summary>Like the original app: a short generated title replaces the first prompt (and is stored with the session).</summary>
    private async Task GenerateTitleAsync(IAgentBackend backend, string prompt)
    {
        try
        {
            var title = await backend.GenerateTitleAsync(prompt, persist: true);
            if (!string.IsNullOrWhiteSpace(title))
            {
                Title = title;
                _main.OnSessionTitleChanged(this);
            }
        }
        catch (Exception)
        {
            // The first-prompt title stays.
        }
    }

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
            return;
        }

        Model = id;
        if (_backend is { IsProcessRunning: true } backend && !await backend.SetModelAsync(id))
        {
            _restartRequired = true;
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
        if (on == ReferenceEquals(_remoteOnBackend, backend))
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

        foreach (var thinking in Items.OfType<ThinkingItem>())
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
                    Items.Add(new UserMessageItem(u.Text, u.Uuid, u.TimestampUtc ?? fallbackTimeUtc, seen: true) { ParentUuid = u.ParentUuid });
                    break;
                case HistoryAssistantText a:
                    CloseGroup();
                    Items.Add(new AssistantTextItem(a.MessageId, -1, a.Text, streaming: false));
                    break;
                case HistoryThinking t:
                    CloseGroup();
                    Items.Add(new ThinkingItem("", -1, t.Text, streaming: false));
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
                    Title = SessionTitles.Shorten(first.Text);
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
        backend.EventReceived += e => Dispatcher.UIThread.Post(() => Apply(backend, e));
        await backend.StartAsync(new AgentStartOptions
        {
            WorkingDirectory = Folder,
            Model = Model,
            PermissionMode = PermissionMode,
            Effort = string.IsNullOrEmpty(Effort) ? null : Effort,
            ThinkingDisplay = "summarized",
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

                break;

            case AccountInfoEvent a:
                _main.OnAccountInfo(a);
                break;

            case TurnStartedEvent:
                IsTurnRunning = true;
                State = AgentState.Running;
                _turnToolCount = 0;
                break;

            case RemotePromptEvent remote:
                if (IsNew)
                {
                    Title = SessionTitles.Shorten(remote.Text);
                    IsNew = false;
                }

                CloseGroup();
                Items.Add(new UserMessageItem(remote.Text, remote.Uuid, DateTime.UtcNow, seen: true) { IsRemote = true });
                IsTurnRunning = true;
                State = AgentState.Running;
                HasRun = true;
                _turnToolCount = 0;
                ContentChanged?.Invoke();
                _main.OnSessionActivity(this);
                break;

            case RemoteControlStateEvent bridge:
                RemoteControlState = bridge.State;
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
                break;

            case ToolCallEvent call when call.ParentToolUseId is null:
                var item = _calls.TryGetValue(call.ToolUseId, out var existing) ? existing : AddCall(call.ToolUseId, call.Name);
                item.SetInput(call.Input);
                _groupOfCall[call.ToolUseId].Refresh();
                break;

            case ToolResultEvent r when r.ParentToolUseId is null:
                CompleteCall(r.ToolUseId, r.IsError, r.Text, r.Structured);
                ContentChanged?.Invoke();
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
                _main.OnAliveChanged(this);
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

        CloseGroup();
        TranscriptItem item = kind == AgentBlockKind.Thinking
            ? new ThinkingItem(messageId, index, "", streaming: true)
            : new AssistantTextItem(messageId, index, "", streaming: true);
        _blocks[(messageId, index)] = item;
        Items.Add(item);
        ContentChanged?.Invoke();
        return item;
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
                        Items.Remove(thinking);
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
            _openGroup = new ToolGroupItem();
            Items.Add(_openGroup);
        }

        var call = new ToolCallItem(toolUseId, name, Folder) { Status = ToolStatus.Running };
        _openGroup.Calls.Add(call);
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
                        Items.Remove(th);
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
        var context = result.InputTokens + result.CacheReadTokens + result.CacheCreationTokens + result.OutputTokens;
        if (context > 0)
        {
            ContextTokens = context;
            ContextFraction = Math.Clamp(context / (double)Catalog.ContextWindow(Model), 0, 1);
        }

        ContentChanged?.Invoke();
        _main.OnTurnCompleted(this);
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
