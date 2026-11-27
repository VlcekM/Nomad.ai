using Avalonia.Threading;
using Nomad.Core.Agents;

namespace Nomad.App.ViewModels;

/// <summary>
/// The app's side of Nomad's agent tools (<see cref="AgentTools"/>): which chats may use them (the ones open here), and where a
/// sent file or a notification shows: the chat's transcript, which the phone view and the phone app read too, and for a
/// notification a Windows notification as well.
/// </summary>
internal sealed class AgentDesk(MainViewModel main) : IAgentDesk
{
    /// <summary>
    /// The calling chat, when it is open here with a session: with its permission mode now, and its Stop.
    /// </summary>
    public Task<DeskChat?> FindAsync(string? callerKey) =>
        Dispatcher.UIThread.InvokeAsync(() => Find(callerKey) is { } s
            ? new DeskChat(new ChatInfo(s.SessionId!, s.TitleOrPlaceholder, s.BackendName, s.Folder, s.IsTurnRunning ? "working" : "idle"),
                s.PermissionMode, s.AgentToolsStopped)
            : null).GetTask();

    /// <summary>
    /// Shows the kept file in the calling chat.
    /// </summary>
    public Task ShowFileAsync(string callerKey, string path, string? caption) =>
        Dispatcher.UIThread.InvokeAsync(() => Find(callerKey)?.ShowSentFile(path, caption)).GetTask();

    /// <summary>
    /// A line in the calling chat and a Windows notification that opens it on a click.
    /// </summary>
    public Task<string> NotifyAsync(string callerKey, string title, string message) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Find(callerKey) is not { } chat) return "nowhere: the chat is no longer open.";

            chat.ShowNotification(title, message);
            Infrastructure.WindowsToast.Show(title, message, chat.SessionId);
            return "a Windows notification on the PC, and a line in this chat (which the phone view and the phone app show).";
        }).GetTask();

    /// <summary>
    /// The open chat with this key; null when there is none.
    /// </summary>
    private SessionViewModel? Find(string? callerKey) =>
        callerKey is null ? null : main.ReachableChats.FirstOrDefault(s => s.ChatKey == callerKey);
}
