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
        SessionId is { } id ? _main.Hub.EnsureHostAsync(Backend, id) : Task.FromResult<RelayBackend?>(null);

    /// <summary>Starts streaming a process this PC just started.</summary>
    private void AttachRelay(IAgentBackend backend)
    {
        DetachRelay();
        _relay = _main.Hub.Publish(Backend, () => SessionId, backend);
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

    /// <summary>Just opened: if another PC runs it right now and streams it, its turn shows here as it happens.</summary>
    public async Task WatchIfLiveAsync()
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
        _relay?.Publish(new RelayAnsweredEvent(item.Request.RequestId, _main.Settings.HubDeviceName ?? Environment.MachineName));

    /// <summary>A prompt the session took on the PC running it (or one this PC relayed, which shows already).</summary>
    private void ShowRelayedPrompt(RelayPromptEvent prompt)
    {
        _stopRequested = false;
        CloseGroup();
        if (IsNew)
        {
            Title = SessionTitles.Shorten(prompt.Text);
            IsNew = false;
        }

        var (text, files) = AttachedFiles.Split(prompt.Text);
        Items.Add(new UserMessageItem(text, prompt.Uuid, DateTime.UtcNow, seen: true) { Files = files });
        IsTurnRunning = true;
        State = AgentState.Running;
        HasRun = true;
        _turnToolCount = 0;
        ContentChanged?.Invoke();
        _main.OnSessionActivity(this);
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
        if (!CanReloadFromStore || Backend != AgentBackends.ClaudeCode || SessionId is not { } id || _main.Store.FindTranscript(id) is not { } file)
        {
            return;
        }

        var history = await Task.Run(() => SessionStore.LoadHistory(file));
        if (CanReloadFromStore)
        {
            // Relayed turns showed without being in the file: the file's version replaces them all.
            if (_relayed)
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
