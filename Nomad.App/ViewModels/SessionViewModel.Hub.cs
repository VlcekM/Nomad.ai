using Avalonia.Threading;
using Nomad.App.Infrastructure;
using Nomad.Core.Agents;
using Nomad.Core.Hub;
using Nomad.Core.Sessions;

namespace Nomad.App.ViewModels;

// Nomad Hub's side of a session (docs/hub.md §5, §8): this PC runs it only while it holds the session's lease, and
// streams it then; while another PC runs it, this one can watch it live and prompt it there.
public sealed partial class SessionViewModel
{
    private RelayPublisher? _relay;

    /// <summary>The transcript shows relayed turns that aren't in the stored file yet: the next reload starts over.</summary>
    private bool _relayed;

    /// <summary>Watching the session another PC runs (through the hub), not running it here.</summary>
    public bool IsRelayViewer => _backend is RelayBackend;

    /// <summary>
    /// Before the CLI starts on a stored session: takes its lease. Returns the other PC's stream when that PC runs it
    /// and streams it; throws (shown in the transcript) when it runs it without.
    /// </summary>
    private Task<RelayBackend?> EnsureHubHostAsync() =>
        SessionId is { } id ? _main.Hub.EnsureHostAsync(Backend, id, ShowHubNotice) : Task.FromResult<RelayBackend?>(null);

    /// <summary>Starts streaming a process this PC just started.</summary>
    private void AttachRelay(IAgentBackend backend)
    {
        DetachRelay();
        _relay = _main.Hub.Publish(Backend, () => SessionId, backend);
        PublishContext();
    }

    private void DetachRelay()
    {
        if (_relay is { } relay)
        {
            _relay = null;
            _ = relay.DisposeAsync().AsTask();
        }
    }

    /// <summary>Watches the session on the PC that runs it: its events come in as if from a process here.</summary>
    private async Task StartRelayAsync(RelayBackend relay)
    {
        // The transcript up to the turn running now, which the stream adds to: shown when it is here (between turns).
        if (SessionId is { } id)
        {
            _ = _main.Hub.FetchWatchedAsync(Backend, id);
        }

        // Set first: the turn so far arrives while it starts, and Apply takes only the current backend's events.
        _backend = relay;
        relay.EventReceived += e => Dispatcher.UIThread.Post(() => Apply(relay, e));
        relay.EventReceived += e =>
        {
            if (e is ProcessExitedEvent)
            {
                Dispatcher.UIThread.Post(() => _ = ReloadFromStoreAsync());
            }
        };
        try
        {
            await relay.StartAsync(new AgentStartOptions { WorkingDirectory = Folder ?? "" });
        }
        catch
        {
            if (ReferenceEquals(_backend, relay))
            {
                _backend = null;
            }

            throw;
        }

        _relayed = true;
        LastActivityUtc = DateTime.UtcNow;
        ShowHubNotice(LiveNotice(relay));
        _main.OnAliveChanged(this);
    }

    private static string LiveNotice(RelayBackend relay) => $"Live from {relay.HostName ?? "another PC"}: prompts, answers and Stop go there.";

    /// <summary>A watch being started on opening; a prompt meanwhile waits for it rather than starting a second one.</summary>
    private Task? _watchStarting;

    private async Task AwaitWatchStartingAsync()
    {
        if (_watchStarting is { } starting)
        {
            await starting;
        }
    }

    /// <summary>Just opened: if another PC runs it right now and streams it, its turn shows here as it happens.</summary>
    public Task WatchIfLiveAsync()
    {
        if (_watchStarting is { IsCompleted: false } running)
        {
            return running;
        }

        return _watchStarting = WatchAsync();

        async Task WatchAsync()
        {
            if (_backend is not null || SessionId is not { } id || await _main.Hub.WatchAsync(Backend, id) is not { } relay || _backend is not null)
            {
                return;
            }

            try
            {
                await StartRelayAsync(relay);
            }
            catch (Exception)
            {
                // It stopped streaming meanwhile: the stored transcript is what there is.
            }
        }
    }

    /// <summary>A watching PC's prompt, answer, Stop or setting, done here as if typed here.</summary>
    public async Task ApplyRelayInputAsync(RelayInput input, string from)
    {
        if (_backend is not { IsProcessRunning: true } backend || backend is RelayBackend)
        {
            return;
        }

        try
        {
            switch (input.Kind)
            {
                // Into the turn running here, as one typed here would go (Claude Code takes it in at its next step).
                case RelayInput.Prompt when input.Text is { } text && CanSteer && !text.TrimStart().StartsWith('/') && backend is ClaudeCodeBackend claude:
                    var steeredUuid = input.Uuid ?? Guid.NewGuid().ToString();
                    var (shown, files) = AttachedFiles.Split(text);
                    var steered = new UserMessageItem(shown, null, DateTime.UtcNow, seen: false) { Files = files, FromDevice = from, IsSteered = true };
                    CloseGroup();
                    Items.Add(steered);
                    var decoded = (input.Images ?? []).Select((i, n) => PromptImages.FromBytes(i.Data, i.MediaType, $"image {n + 1}")).ToList();
                    List<Attachment> attachments = [.. files, .. decoded.OfType<Attachment>()];
                    _steered[steeredUuid] = steered;
                    _steeredInput[steeredUuid] = (shown, attachments, from);
                    ContentChanged?.Invoke();
                    _main.OnSessionActivity(this);
                    try
                    {
                        await claude.SteerAsync(text, input.Images, steeredUuid);
                    }
                    catch (Exception)
                    {
                        // Not lost: it waits in the queue, as one typed here would, and goes out with the next prompt.
                        _steered.Remove(steeredUuid);
                        _steeredInput.Remove(steeredUuid);
                        Items.Remove(steered);
                        if (attachments.Count < files.Count + decoded.Count)
                        {
                            var left = files.Count + decoded.Count - attachments.Count;
                            Items.Add(new ErrorItem($"{(left == 1 ? "An image" : $"{left} images")} sent from {from} could not be read, so the queued prompt goes without {(left == 1 ? "it" : "them")}."));
                        }

                        Queue(shown, attachments, from);
                        if (!IsTurnRunning)
                        {
                            await SendQueuedAsync();
                        }

                        ContentChanged?.Invoke();
                        break;
                    }

                    _relay?.PublishPrompt(text, steeredUuid, from);
                    await BackgroundForWaitingPromptAsync();
                    break;

                case RelayInput.Prompt when input.Text is { } text:
                    var uuid = input.Uuid ?? Guid.NewGuid().ToString();
                    ShowRelayedPrompt(new RelayPromptEvent(text, uuid, from));
                    _relay?.ExpectPrompt(uuid, from);
                    await backend.SendPromptAsync(text, uuid, input.Images);
                    break;

                case RelayInput.Answer when input.Request is { } request && input.Decision is { } decision:
                    var card = Items.OfType<PermissionItem>().FirstOrDefault(i => i.Request.RequestId == request.RequestId && !i.IsAnswered);
                    if (card is null)
                    {
                        return; // Answered here already.
                    }

                    card.MarkAnswered("Answered on " + from);
                    PendingPermission = Items.OfType<PermissionItem>().LastOrDefault(i => !i.IsAnswered);
                    await backend.RespondAsync(card.Request, decision);
                    _relay?.Publish(new RelayAnsweredEvent(request.RequestId, from));
                    break;

                case RelayInput.Interrupt:
                    await StopAsync();
                    break;

                case RelayInput.Model when input.Value is { } model:
                    await SwitchModelAsync(model);
                    _relay?.Publish(new ModelChangedEvent(model));
                    break;

                case RelayInput.Mode when input.Value is { } mode:
                    await SelectModeAsync(mode);
                    _relay?.Publish(new PermissionModeChangedEvent(mode));
                    break;

                case RelayInput.Effort:
                    await ApplyEffortAsync(input.Value ?? "");
                    break;

                case RelayInput.Fast when input.Flag is { } fast && fast != FastMode:
                    await ToggleFastModeAsync();
                    break;

                case RelayInput.StopTask when input.Value is { } task:
                    await backend.StopTaskAsync(task);
                    break;
            }
        }
        catch (Exception ex)
        {
            Items.Add(new ErrorItem($"Could not do what {from} sent: {ex.Message}"));
            ContentChanged?.Invoke();
        }
    }

    /// <summary>This PC answered a card: the watching PCs close theirs.</summary>
    private void PublishAnswered(PermissionItem item) =>
        _relay?.Publish(new RelayAnsweredEvent(item.Request.RequestId, _main.Hub.ThisPcName));

    /// <summary>A prompt the session took on the PC running it (or one this PC relayed, which shows already).</summary>
    private void ShowRelayedPrompt(RelayPromptEvent prompt)
    {
        // Watching, a prompt the host sent into the turn running there: a prompt in that turn, which goes on as it was.
        if (IsRelayViewer && IsTurnRunning)
        {
            var (steeredText, steeredFiles) = AttachedFiles.Split(prompt.Text);
            CloseGroup();
            Items.Add(new UserMessageItem(steeredText, null, DateTime.UtcNow, seen: true) { Files = steeredFiles, FromDevice = prompt.From, IsSteered = true });
            ContentChanged?.Invoke();
            _main.OnSessionActivity(this);
            return;
        }

        _stopRequested = false;
        CloseGroup();
        if (IsNew)
        {
            Title = SessionTitles.Shorten(prompt.Text);
            IsNew = false;
        }

        var (text, files) = AttachedFiles.Split(prompt.Text);
        Items.Add(new UserMessageItem(text, prompt.Uuid, DateTime.UtcNow, seen: true) { Files = files, FromDevice = prompt.From });
        IsTurnRunning = true;
        State = AgentState.Running;
        HasRun = true;
        _turnToolCount = 0;
        ContentChanged?.Invoke();
        _main.OnSessionActivity(this);
    }

    /// <summary>The window the context meter measures against: the host's while watching it, else the model's.</summary>
    private long ContextWindowSize => _relayWindow is { } window && IsRelayViewer ? window : Catalog.ContextWindow(Model);

    private long? _relayWindow;

    /// <summary>The PC running the session sent its context meter.</summary>
    private void ShowRelayedContext(RelayContextEvent context)
    {
        _relayWindow = context.Window > 0 ? context.Window : null;
        ContextTokens = context.Tokens;
        ContextFraction = context.Tokens > 0 ? Math.Clamp(context.Tokens / (double)ContextWindowSize, 0, 1) : 0;
        OnPropertyChanged(nameof(ContextAmountText));
        OnPropertyChanged(nameof(ContextToolTip));
    }

    /// <summary>The model changed: the meter measures against its window, here and on the watching PCs.</summary>
    private void RefreshContextWindow()
    {
        if (IsRelayViewer)
        {
            return; // The host sends its window.
        }

        ContextFraction = ContextTokens > 0 ? Math.Clamp(ContextTokens / (double)ContextWindowSize, 0, 1) : 0;
        OnPropertyChanged(nameof(ContextAmountText));
        OnPropertyChanged(nameof(ContextToolTip));
        PublishContext();
    }

    // The meter changed here: the watching PCs show it too.
    partial void OnContextTokensChanged(long value) => PublishContext();

    private void PublishContext()
    {
        if (_relay is { } relay && !IsRelayViewer)
        {

            relay.PublishStanding(new RelayContextEvent(ContextTokens, ContextWindowSize));
        }
    }

    /// <summary>A card answered on another PC.</summary>
    private void OnRelayAnswered(RelayAnsweredEvent answered)
    {
        foreach (var card in Items.OfType<PermissionItem>().Where(i => i.Request.RequestId == answered.RequestId && !i.IsAnswered))
        {
            card.MarkAnswered("Answered on " + answered.By);
        }

        PendingPermission = Items.OfType<PermissionItem>().LastOrDefault(i => !i.IsAnswered);
    }

    /// <summary>Idle here, or watched between turns: a newer version from the hub may replace the transcript.</summary>
    public bool CanReloadFromStore => !IsProcessRunning || (IsRelayViewer && !IsTurnRunning);

    /// <summary>A newer version came from the hub while this session sat idle: the new turns join the transcript.</summary>
    public async Task ReloadFromStoreAsync()
    {
        if (!CanReloadFromStore || SessionId is not { } id)
        {
            return;
        }

        SessionHistory history;
        var whole = _relayed;
        if (Backend == AgentBackends.ClaudeCode && _main.Store.FindTranscript(id) is { } file)
        {
            history = await Task.Run(() => SessionStore.LoadHistory(file));
        }
        else if (Backend == AgentBackends.Antigravity && Folder is { } folder)
        {
            try
            {
                history = await Nomad.Core.Antigravity.AntigravityHistory.LoadAsync(id, folder);
            }
            catch (Exception)
            {
                return; // The CLI couldn't replay it: the transcript stays as it was until the chat is opened again.
            }

            // The CLI replays the whole chat, which turns run in this window aren't counted against: it is shown again whole.
            whole = true;
        }
        else
        {
            return;
        }

        if (CanReloadFromStore)
        {
            // Relayed turns showed without being in the file: the file's version replaces them all.
            if (whole)
            {
                _relayed = false;
                Items.Clear();
                _calls.Clear();
                _blocks.Clear();
                _historyCount = 0;
                _openGroup = null;
                _unseen = null;
            }

            AppendHistory(history, DateTime.UtcNow);
            if (_backend is RelayBackend relay && Items.LastOrDefault() is not NoticeItem)
            {
                _relayed = true; // Still watching: the next turns come live again.
                ShowHubNotice(LiveNotice(relay));
            }

            ContentChanged?.Invoke();
        }
    }

    public void ShowHubNotice(string text)
    {
        Items.Add(new NoticeItem(text));
        ContentChanged?.Invoke();
    }
}
