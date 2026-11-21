using Avalonia.Threading;
using Nomad.Core.Agents;

namespace Nomad.App.ViewModels;

/// <summary>
/// The chats open in this window, for the chat tools: what <c>list_chats</c> shows and where <c>send_to_chat</c> delivers. A message
/// goes through the target chat's own queue, so it never cuts into a running turn.
/// </summary>
internal sealed class ChatDirectory(MainViewModel main) : IChatDirectory
{
    // A loop between two chats is stopped here: a chat may send this many messages in this window.
    private const int MaxSends = 10;
    private static readonly TimeSpan SendWindow = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, Queue<DateTime>> _sends = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<ChatInfo>> ListAsync(string? callerKey) => Dispatcher.UIThread.InvokeAsync<IReadOnlyList<ChatInfo>>(() =>
        Chats().Where(s => s.ChatKey != callerKey).Select(Info).ToList()).GetTask();

    public Task<string> SendAsync(string? callerKey, string target, string message) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var sender = Chats().FirstOrDefault(s => s.ChatKey == callerKey);
        var chats = Chats().Where(s => !ReferenceEquals(s, sender)).ToList();
        var matches = chats.Where(s => string.Equals(s.SessionId, target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            matches = chats.Where(s => string.Equals(s.Title.Trim(), target, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matches.Count == 0)
        {
            throw new ChatMessageException($"No open chat has the id or title \"{target}\". Call list_chats for the open ones.");
        }

        if (matches.Count > 1)
        {
            throw new ChatMessageException($"{matches.Count} chats are titled \"{target}\". Use an id from list_chats.");
        }

        if (callerKey is not null && !TryCountSend(callerKey))
        {
            throw new ChatMessageException($"This chat has sent {MaxSends} messages in the last {SendWindow.TotalMinutes:0} minutes. " +
                "Stop and tell the user, in case the chats are answering each other in a loop.");
        }

        var chat = matches[0];
        var busy = chat.IsTurnRunning;
        await chat.ReceiveMessageAsync(ChatTools.Envelope(sender is null ? null : Info(sender), message));
        return busy
            ? $"Queued for \"{chat.Title}\": it is in a turn and gets the message when that ends."
            : $"Delivered to \"{chat.Title}\", which is working on it now.";
    });

    private IEnumerable<SessionViewModel> Chats() =>
        main.OpenChats.Where(s => !s.IsNew && !s.IsPhoneSession && !string.IsNullOrEmpty(s.SessionId));

    private static ChatInfo Info(SessionViewModel s) => new(s.SessionId!, s.TitleOrPlaceholder, s.BackendName, s.Folder,
        s.IsTurnRunning ? "working" : s.IsProcessRunning ? "idle" : "stopped");

    private bool TryCountSend(string callerKey)
    {
        if (!_sends.TryGetValue(callerKey, out var times))
        {
            _sends[callerKey] = times = new Queue<DateTime>();
        }

        var now = DateTime.UtcNow;
        while (times.Count > 0 && now - times.Peek() > SendWindow)
        {
            times.Dequeue();
        }

        if (times.Count >= MaxSends)
        {
            return false;
        }

        times.Enqueue(now);
        return true;
    }
}
