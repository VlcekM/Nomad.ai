using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.App.Infrastructure;
using Nomad.Core.Codex;
using Nomad.Core.Sessions;
using Nomad.Core.Usage;

namespace Nomad.App.ViewModels;

/// <summary>Monthly token counts from the conversation logs on this computer.</summary>
public sealed partial class LocalTokenUsageViewModel : ObservableObject
{
    private bool _reading;
    private DateTime _readAt;

    [ObservableProperty] public partial string ClaudeText { get; set; } = "Reading local tokens…";
    [ObservableProperty] public partial string CodexText { get; set; } = "Reading local tokens…";

    public async Task RefreshAsync(bool force = false)
    {
        if (_reading || (!force && DateTime.UtcNow - _readAt < TimeSpan.FromMinutes(1))) return;
        _reading = true;
        try
        {
            var now = DateTimeOffset.Now;
            var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(now.Year, now.Month, 1)));
            async Task ReadClaudeAsync() => ClaudeText = Display(await Task.Run(() => LocalTokenUsage.Claude(SessionStore.DefaultProjectsRoot, start)));
            async Task ReadCodexAsync() => CodexText = Display(await Task.Run(() => LocalTokenUsage.Codex(CodexServer.CodexHome, start)));
            await Task.WhenAll(ReadClaudeAsync(), ReadCodexAsync());
            _readAt = DateTime.UtcNow;
        }
        finally { _reading = false; }
    }

    private static string Display(LocalTokens t) => $"Local this month · {Format.Tokens(t.Input)} in · {Format.Tokens(t.Output)} out";
}
