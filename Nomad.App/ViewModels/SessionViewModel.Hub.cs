using Nomad.Core.Agents;
using Nomad.Core.Sessions;

namespace Nomad.App.ViewModels;

// Nomad Hub's side of a session (docs/hub.md §5): this PC runs it only while it holds the session's lease.
public sealed partial class SessionViewModel
{
    /// <summary>Before the CLI starts on a stored session: takes its lease (throws, shown in the transcript, when another PC runs it).</summary>
    private Task EnsureHubHostAsync() =>
        SessionId is { } id ? _main.Hub.EnsureHostAsync(Backend, id) : Task.CompletedTask;

    /// <summary>A newer version came from the hub while this session sat idle: the new turns join the transcript.</summary>
    public async Task ReloadFromStoreAsync()
    {
        if (IsProcessRunning || Backend != AgentBackends.ClaudeCode || SessionId is not { } id || _main.Store.FindTranscript(id) is not { } file)
        {
            return;
        }

        var history = await Task.Run(() => SessionStore.LoadHistory(file));
        if (!IsProcessRunning)
        {
            AppendHistory(history, DateTime.UtcNow);
            ContentChanged?.Invoke();
        }
    }

    public void ShowHubNotice(string text)
    {
        Items.Add(new NoticeItem(text));
        ContentChanged?.Invoke();
    }
}
