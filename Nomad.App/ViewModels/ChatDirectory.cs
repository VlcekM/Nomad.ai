using Avalonia.Threading;
using Nomad.Core.Agents;

namespace Nomad.App.ViewModels;

/// <summary>
/// The chats open in this window, for the chat tools: what <c>list_chats</c> shows and where <c>send_to_chat</c> delivers. A message
/// goes into a Claude Code chat's running turn (read at its next step, as one typed there would be), else through the target
/// chat's own queue.
/// </summary>
internal sealed class ChatDirectory(MainViewModel main) : IChatDirectory
{
    #region Listing and delivery

    /// <summary>
    /// Lists the open chats other than the caller's.
    /// </summary>
    public Task<IReadOnlyList<ChatInfo>> ListAsync(string? callerKey) =>
        Dispatcher.UIThread.InvokeAsync<IReadOnlyList<ChatInfo>>(() => List(callerKey)).GetTask();

    /// <summary>
    /// Delivers <paramref name="message"/> to the chat with the id or exact title <paramref name="target"/>, on the UI thread.
    /// </summary>
    public Task<string> SendAsync(string? callerKey, string target, string message) =>
        Dispatcher.UIThread.InvokeAsync(() => SendOnUiAsync(callerKey, target, message));

    /// <summary>
    /// The open chats other than the caller's, as the tools describe them.
    /// </summary>
    private IReadOnlyList<ChatInfo> List(string? callerKey) => Chats()
        .Where(s => s.ChatKey != callerKey)
        .Select(Info)
        .ToList();

    /// <summary>
    /// Finds the target, counts the send against the caller's limit and delivers the message there; returns what to tell the sender.
    /// </summary>
    private async Task<string> SendOnUiAsync(string? callerKey, string target, string message)
    {
        var sender = Chats().FirstOrDefault(s => s.ChatKey == callerKey);
        var chat = FindTarget(sender, target);

        if (callerKey is not null && !TryCountSend(callerKey))
        {
            throw new ChatMessageException($"This chat has sent {MaxSends} messages in the last {SendWindow.TotalMinutes:0} minutes. " +
                "Stop and tell the user, in case the chats are answering each other in a loop.");
        }

        return await chat.ReceiveMessageAsync(ChatTools.Envelope(sender is null ? null : Info(sender), message)) switch
        {
            MessageDelivery.IntoTurn => $"Delivered into \"{chat.Title}\"'s running turn: it reads it at its next step.",
            MessageDelivery.Queued => $"Queued for \"{chat.Title}\": it is in a turn and gets the message when that ends.",
            MessageDelivery.RunningElsewhere => throw new ChatMessageException(
                $"\"{chat.Title}\" is running in another Nomad window or a terminal, which keeps it. Nothing was sent."),
            MessageDelivery.Failed => throw new ChatMessageException(
                $"\"{chat.Title}\" could not be sent the message: {(chat.Items.LastOrDefault() is ErrorItem error ? error.Message : "its agent did not start")}."),
            _ => $"Delivered to \"{chat.Title}\", which is working on it now.",
        };
    }

    /// <summary>
    /// The one chat other than <paramref name="sender"/> with the id <paramref name="target"/>, or else with that exact title; throws
    /// <see cref="ChatMessageException"/> when there is none or more than one.
    /// </summary>
    private SessionViewModel FindTarget(SessionViewModel? sender, string target)
    {
        var chats = Chats().Where(s => !ReferenceEquals(s, sender)).ToList();
        var matches = chats.Where(s => string.Equals(s.SessionId, target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = chats.Where(s => string.Equals(s.Title.Trim(), target, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
            throw new ChatMessageException($"No open chat has the id or title \"{target}\". Call list_chats for the open ones.");

        if (matches.Count > 1) throw new ChatMessageException($"{matches.Count} chats are titled \"{target}\". Use an id from list_chats.");

        return matches[0];
    }

    /// <summary>
    /// The chats the tools can reach: opened, with a session id, and not a phone session.
    /// </summary>
    private IEnumerable<SessionViewModel> Chats() => main.ReachableChats;

    /// <summary>
    /// How the tools describe <paramref name="s"/>.
    /// </summary>
    private static ChatInfo Info(SessionViewModel s) => new(s.SessionId!, s.TitleOrPlaceholder, s.BackendName, s.Folder, State(s));

    /// <summary>
    /// What <paramref name="s"/> is doing: working, idle (its agent is running) or stopped.
    /// </summary>
    private static string State(SessionViewModel s) => s.IsTurnRunning
        ? "working"
        : s.IsProcessRunning
            ? "idle"
            : "stopped";

    #endregion

    #region Send limit

    /// <summary>
    /// How many messages a chat may send in <see cref="SendWindow"/>: a loop between two chats is stopped here.
    /// </summary>
    private const int MaxSends = 10;

    /// <summary>
    /// The window <see cref="MaxSends"/> counts in.
    /// </summary>
    private static readonly TimeSpan SendWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When each chat (by its key) sent its messages within the window, oldest first.
    /// </summary>
    private readonly Dictionary<string, Queue<DateTime>> _sends = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts one more message from the chat with <paramref name="callerKey"/>; false when it has reached the limit.
    /// </summary>
    private bool TryCountSend(string callerKey)
    {
        if (!_sends.TryGetValue(callerKey, out var times)) _sends[callerKey] = times = new Queue<DateTime>();

        var now = DateTime.UtcNow;
        while (times.Count > 0 && now - times.Peek() > SendWindow)
        {
            times.Dequeue();
        }

        if (times.Count >= MaxSends) return false;

        times.Enqueue(now);
        return true;
    }

    #endregion
}
