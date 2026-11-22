using System.Text.Json;
using Avalonia.Threading;
using Nomad.Core.Agents;
using Nomad.Core.Hub;
using Nomad.Hub.Contracts;

namespace Nomad.App.ViewModels;

/// <summary>
/// This PC's side of the phone (docs/mobile.md): lists the chats open here on the hub's board, and answers for them when the
/// phone asks for a page of a transcript or sends a prompt, an answer or Stop. Everything runs on the UI thread, under the same
/// checks the window uses: an answer must name the card waiting now, and a question's answers are checked whole before any of
/// them is applied.
/// </summary>
internal sealed class MobileHost : IDisposable
{
    /// <summary>How often the board is checked for changes (it is sent only when it changed).</summary>
    private static readonly TimeSpan BoardInterval = TimeSpan.FromSeconds(1);

    /// <summary>How much of one entry's text is sent at most.</summary>
    private const int MaxText = 12_000;

    private readonly MainViewModel _main;
    private readonly HubClient _client;
    private readonly Func<bool> _enabled;
    private readonly DispatcherTimer _timer;
    private string? _lastBoard;
    private bool _publishing;

    /// <param name="enabled">Whether the phone may see this PC's chats now (the hub relays and Stream live sessions is on).</param>
    public MobileHost(MainViewModel main, HubClient client, Func<bool> enabled)
    {
        _main = main;
        _client = client;
        _enabled = enabled;
        _timer = new DispatcherTimer(BoardInterval, DispatcherPriority.Background, (_, _) => _ = PublishAsync());
        client.HistoryRequested = (backend, id, before, limit) => OnUiAsync(() => Page(backend, id, before, limit));
        client.InputRequested = (backend, id, input, from) => OnUiTaskAsync(() => ApplyAsync(backend, id, input, from));
        client.ConnectionChanged += OnConnectionChanged;
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _client.ConnectionChanged -= OnConnectionChanged;
        _client.HistoryRequested = null;
        _client.InputRequested = null;
    }

    /// <summary>A new connection starts with nothing on the board: the next tick sends it all again.</summary>
    private void OnConnectionChanged(bool up) => Dispatcher.UIThread.Post(() => _lastBoard = null);

    private static Task<T> OnUiAsync<T>(Func<T> work) => Dispatcher.UIThread.InvokeAsync(work).GetTask();

    private static Task<T> OnUiTaskAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);

    // ------------------------------------------------------------------ the board

    private async Task PublishAsync()
    {
        if (_publishing || !_client.IsConnected || !_enabled())
        {
            return;
        }

        var chats = _main.ReachableChats.Select(Board).ToList();
        var json = JsonSerializer.Serialize(chats, HubJson.Default.IReadOnlyListBoardChat);
        if (json == _lastBoard)
        {
            return;
        }

        _publishing = true;
        try
        {
            await _client.BoardPublishAsync(chats);
            _lastBoard = json;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.AspNetCore.SignalR.HubException or IOException or TaskCanceledException)
        {
            // Not connected, or the hub has no relay: tried again on the next tick.
        }
        finally
        {
            _publishing = false;
        }
    }

    private static BoardChat Board(SessionViewModel s) => new(s.Backend, s.SessionId!, s.TitleOrPlaceholder, s.BackendName, s.Folder, State(s),
        Waiting(s), Clip(LastLine(s), 200), s.LastActivityUtc, s.Queued.Count);

    private static string State(SessionViewModel s) =>
        s.PendingPermission is { IsAnswered: false } ? "waiting" : s.IsTurnRunning ? "working" : s.IsProcessRunning ? "idle" : "stopped";

    private static string? Waiting(SessionViewModel s) => s.PendingPermission switch
    {
        { IsAnswered: true } or null => null,
        QuestionItem => "A question",
        { } p => Clip(p.Title, 120),
    };

    private static string LastLine(SessionViewModel s)
    {
        for (var i = s.Items.Count - 1; i >= 0; i--)
        {
            switch (s.Items[i])
            {
                case AssistantTextItem a when a.Text.Length > 0:
                    return a.Text;
                case UserMessageItem u:
                    return "You: " + u.DisplayText;
            }
        }

        return "";
    }

    private SessionViewModel? Find(string backend, string id) =>
        _main.ReachableChats.FirstOrDefault(s => s.Backend == backend && s.SessionId == id);

    // ------------------------------------------------------------------ transcript pages

    private ChatPage Page(string backend, string id, int? before, int limit)
    {
        if (Find(backend, id) is not { } s)
        {
            throw new InvalidOperationException("That chat is no longer open in Nomad.");
        }

        var end = Math.Clamp(before ?? s.Items.Count, 0, s.Items.Count);
        var entries = new List<ChatEntry>();
        for (var i = end - 1; i >= 0 && entries.Count < limit; i--)
        {
            if (Entry(i, s.Items[i], ReferenceEquals(s.Items[i], s.PendingPermission)) is { } entry)
            {
                entries.Add(entry);
            }
        }

        entries.Reverse();
        return new ChatPage(State(s), s.Queued.Count, s.Items.Count, entries);
    }

    private static ChatEntry? Entry(int index, TranscriptItem item, bool pending) => item switch
    {
        UserMessageItem u => new ChatEntry(index, "user", Clip(u.DisplayText), u.IsFromChat ? u.FromChatName : null),
        AssistantTextItem a => new ChatEntry(index, "assistant", Clip(a.Text)),
        ToolGroupItem g => new ChatEntry(index, "activity", g.Summary.Length > 0 ? Clip(g.Summary) : $"{g.Calls.Count} tool call{(g.Calls.Count == 1 ? "" : "s")}"),
        QuestionItem q => new ChatEntry(index, "question", "", Card: new ChatCard(q.Request.RequestId, pending && !q.IsAnswered, null, null, null,
            false, null, null, q.AnswerText, [.. q.Questions.Select(e => new ChatQuestion(Clip(e.Header, 200), Clip(e.Question, 2000), e.MultiSelect,
                [.. e.Options.Select(o => new ChatOption(Clip(o.Label, 500), o.Description is { } d ? Clip(d, 2000) : null))]))])),
        PermissionItem p => new ChatEntry(index, "permission", "", Card: new ChatCard(p.Request.RequestId, pending && !p.IsAnswered, p.Title,
            Clip(p.Detail, 4000), p.Description is { } d ? Clip(d, 2000) : null, p.CanAllowForSession, p.AllowText, p.DenyText, p.AnswerText, null)),
        ErrorItem e => new ChatEntry(index, "error", Clip(e.Message)),
        NoticeItem n => new ChatEntry(index, "notice", Clip(n.Text)),
        SentFilesItem f => new ChatEntry(index, "notice", f.Caption.Length > 0 ? Clip($"Sent files: {f.Caption}") : "Sent files (open Nomad on the PC to see them)"),
        AssistantImagesItem i => new ChatEntry(index, "notice", $"{i.Sources.Count} image{(i.Sources.Count == 1 ? "" : "s")} (open Nomad on the PC to see them)"),
        ArtifactItem art => new ChatEntry(index, "notice", Clip($"Artifact: {art.Title} {art.Url}")),
        NewChatItem chat => new ChatEntry(index, "notice", Clip($"Suggested a new chat: {chat.Title}")),
        _ => null,
    };

    // ------------------------------------------------------------------ input

    private async Task<ChatInputResult> ApplyAsync(string backend, string id, ChatInput input, string from)
    {
        if (!_enabled())
        {
            return new ChatInputResult(false, "This PC isn't sharing its chats right now.");
        }

        if (Find(backend, id) is not { } s)
        {
            return new ChatInputResult(false, "That chat is no longer open in Nomad.");
        }

        switch (input.Kind)
        {
            case "prompt":
                var text = input.Text?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    return new ChatInputResult(false, "Nothing to send.");
                }

                // Answered once queued, not once the agent has started (that can take a while), so a slow start never looks failed.
                var busy = s.IsTurnRunning;
                _ = s.ReceiveMessageAsync(text);
                return new ChatInputResult(true, busy ? "Queued: it is working, and gets this when the turn ends." : "Sent.");

            case "stop":
                if (!s.IsTurnRunning)
                {
                    return new ChatInputResult(false, "It isn't working on anything.");
                }

                await s.StopAsync();
                return new ChatInputResult(true, "Stopped.");

            case "answer":
                return Answer(s, input);

            default:
                return new ChatInputResult(false, "Nomad doesn't know that kind of input.");
        }
    }

    /// <summary>
    /// Answers the waiting card: only the one the phone names, only with a choice it offers, and for a question only when every
    /// question gets a valid answer (nothing on the card changes otherwise).
    /// </summary>
    private static ChatInputResult Answer(SessionViewModel s, ChatInput input)
    {
        if (s.PendingPermission is not { IsAnswered: false } pending || input.Card is null || pending.Request.RequestId != input.Card)
        {
            return new ChatInputResult(false, "That request is no longer the one waiting.");
        }

        PermissionChoice? choice = input.Choice switch
        {
            "allow" => PermissionChoice.Allow,
            "session" when pending.CanAllowForSession && pending is not QuestionItem => PermissionChoice.AllowForSession,
            "deny" => PermissionChoice.Deny,
            _ => null,
        };
        if (choice is not { } picked)
        {
            return new ChatInputResult(false, "That choice isn't offered for this request.");
        }

        if (pending is QuestionItem question && picked != PermissionChoice.Deny)
        {
            if (ReadAnswers(question, input.Answers) is not { } answers)
            {
                return new ChatInputResult(false, "Every question needs an answer: pick an option or type one.");
            }

            ApplyAnswers(question, answers);
        }

        s.AnswerPending(picked);
        return pending.IsAnswered
            ? new ChatInputResult(true, "Answered.")
            : new ChatInputResult(false, s.Items.LastOrDefault() is ErrorItem error ? error.Message : "Nomad didn't take that answer. Look at the chat on the PC.");
    }

    /// <summary>The chosen options and typed text for each question, or null unless every question gets a valid answer.</summary>
    private static List<(HashSet<QuestionOption> Options, string Other)>? ReadAnswers(QuestionItem question, IReadOnlyList<ChatAnswer>? answers)
    {
        if (answers is null || answers.Count != question.Questions.Count)
        {
            return null;
        }

        var result = new List<(HashSet<QuestionOption>, string)>();
        for (var i = 0; i < answers.Count; i++)
        {
            var entry = question.Questions[i];
            var picked = new HashSet<QuestionOption>();
            foreach (var label in answers[i]?.Options ?? [])
            {
                if (entry.Options.FirstOrDefault(o => o.Label == label) is not { } option)
                {
                    return null;
                }

                picked.Add(option);
            }

            var other = (answers[i]?.Other ?? "").Trim();
            if ((picked.Count == 0 && other.Length == 0) || (!entry.MultiSelect && picked.Count + (other.Length > 0 ? 1 : 0) > 1))
            {
                return null;
            }

            result.Add((picked, other));
        }

        return result;
    }

    /// <summary>Puts validated answers on the card through its own Pick and OtherText, so its state stays consistent.</summary>
    private static void ApplyAnswers(QuestionItem question, List<(HashSet<QuestionOption> Options, string Other)> answers)
    {
        for (var i = 0; i < answers.Count; i++)
        {
            var entry = question.Questions[i];
            var (picked, other) = answers[i];
            if (entry.MultiSelect)
            {
                foreach (var option in entry.Options.Where(o => o.IsSelected != picked.Contains(o)).ToList())
                {
                    entry.Pick(option);
                }

                entry.OtherText = other;
            }
            else if (picked.Count == 1)
            {
                entry.Pick(picked.First());
            }
            else
            {
                entry.OtherText = other;
            }
        }
    }

    /// <summary>Cuts text to <paramref name="max"/> characters, never between the two halves of a surrogate pair.</summary>
    internal static string Clip(string text, int max = MaxText)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut] + "…";
    }
}
