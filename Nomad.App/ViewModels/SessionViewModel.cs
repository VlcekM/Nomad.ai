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
    private bool _suppressModeSync;
    private int _turnToolCount;

    public SessionViewModel(MainViewModel main, string? folder)
    {
        _main = main;
        Folder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        Model = main.DefaultModel;
        PermissionMode = main.Settings.DefaultPermissionMode;
        Effort = main.Settings.DefaultEffort;

        foreach (var m in Catalog.Models)
        {
            ModelOptions.Add(new MenuOption(m.Id, m.DisplayName, shortcut: m.Shortcut));
        }

        foreach (var m in Catalog.Modes)
        {
            ModeOptions.Add(new MenuOption(m.Id, m.DisplayName, m.Description, m.Shortcut) { IsDanger = m.Id == "bypassPermissions" });
        }

        foreach (var e in Catalog.Efforts)
        {
            EffortOptions.Add(new MenuOption(e.Id, e.DisplayName));
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

    public ObservableCollection<MenuOption> EffortOptions { get; } = [];

    /// <summary>Recent folders for the new-session card (up to five).</summary>
    public ObservableCollection<MenuOption> CardFolders { get; } = [];

    /// <summary>Recent folders for the folder menu, filtered by <see cref="FolderFilter"/>.</summary>
    public ObservableCollection<MenuOption> MenuFolders { get; } = [];

    public bool HasCardFolders => CardFolders.Count > 0;

    [ObservableProperty]
    public partial string FolderFilter { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEffortExpanded { get; set; }

    public bool IsNoFolder => Folder is null;

    partial void OnModelChanged(string value) => SyncOptions();

    partial void OnPermissionModeChanged(string value) => SyncOptions();

    partial void OnEffortChanged(string value) => SyncOptions();

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

        foreach (var o in EffortOptions)
        {
            o.IsSelected = o.Id == (Effort ?? "");
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

    /// <summary>Raised after items were added or grew, so the view can follow the bottom.</summary>
    public event Action? ContentChanged;

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    public async Task SendAsync()
    {
        var text = ComposerText.Trim();
        if (text.Length == 0 || IsTurnRunning)
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
        Items.Add(new UserMessageItem(text));
        IsTurnRunning = true;
        State = AgentState.Running;
        HasRun = true;
        ContentChanged?.Invoke();
        _main.OnSessionActivity(this);

        try
        {
            await EnsureBackendAsync();
            await _backend!.SendPromptAsync(text);
            if (first)
            {
                _ = GenerateTitleAsync(_backend, text);
            }
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
        foreach (var item in history.Items)
        {
            switch (item)
            {
                case HistoryUserMessage u:
                    CloseGroup();
                    Items.Add(new UserMessageItem(u.Text));
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

        FinishOpenCalls();
        CloseGroup();
        ContentChanged?.Invoke();
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
            ResumeSessionId = SessionId,
            CliPath = _main.Settings.CliPath,
            ExtraArguments = _main.Dev.ExtraCliArguments,
        });
        _backend = backend;
    }

    public async Task ShutdownAsync()
    {
        if (_backend is not null)
        {
            var b = _backend;
            _backend = null;
            await b.DisposeAsync();
        }
    }

    private void Apply(IAgentBackend source, AgentEvent e)
    {
        if (!ReferenceEquals(source, _backend))
        {
            return; // A replaced process still draining.
        }

        switch (e)
        {
            case SessionStartedEvent s:
                if (!string.IsNullOrEmpty(s.SessionId) && s.SessionId != SessionId)
                {
                    SessionId = s.SessionId;
                    _main.OnSessionIdentified(this);
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
