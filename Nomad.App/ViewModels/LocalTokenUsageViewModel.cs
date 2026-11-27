using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using Nomad.App.Infrastructure;
using Nomad.Core.Codex;
using Nomad.Core.Sessions;
using Nomad.Core.Usage;

namespace Nomad.App.ViewModels;

/// <summary>Monthly token counts from the conversation logs on this computer, or of every PC when Nomad Hub is connected.</summary>
public sealed partial class LocalTokenUsageViewModel(string cachePath) : ObservableObject
{
    private readonly LocalTokenCache _cache = new(cachePath, SessionStore.DefaultProjectsRoot, CodexServer.CodexHome);
    private bool _reading;
    private long _cachedMonth;
    private DateTime _readAt;

    [ObservableProperty] public partial string ClaudeText { get; set; } = "Reading local tokens…";
    [ObservableProperty] public partial string CodexText { get; set; } = "Reading local tokens…";

    /// <summary>Every PC's totals since a time, by backend (from Nomad Hub); null or a null answer: this PC's logs.</summary>
    public Func<DateTime, Task<IReadOnlyDictionary<string, (long Input, long Output)>?>>? HubTotals { get; set; }

    public async Task RefreshAsync(bool force = false)
    {
        if (_reading || (!force && DateTime.UtcNow - _readAt < TimeSpan.FromMinutes(1))) return;
        _reading = true;
        try
        {
            var now = DateTimeOffset.Now;
            var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(now.Year, now.Month, 1)));
            if (_cachedMonth != start.UtcTicks)
            {
                _cachedMonth = start.UtcTicks;
                try
                {
                    if (await Task.Run(() => _cache.ReadCached(start)) is { } cached) Show(cached);
                }
                catch (Exception e) when (IsCacheError(e)) { }
            }

            if (HubTotals is { } hub && await hub(start.UtcDateTime) is { } all)
            {
                ClaudeText = Display(all.GetValueOrDefault(Nomad.Core.Agents.AgentBackends.ClaudeCode), "All PCs");
                CodexText = Display(all.GetValueOrDefault(Nomad.Core.Agents.AgentBackends.Codex), "All PCs");
                _readAt = DateTime.UtcNow;
                return;
            }

            Show(await Task.Run(() =>
            {
                try { return _cache.Refresh(start); }
                catch (Exception e) when (IsCacheError(e))
                {
                    return new MonthlyTokenTotals(LocalTokenUsage.Claude(SessionStore.DefaultProjectsRoot, start),
                        LocalTokenUsage.Codex(CodexServer.CodexHome, start));
                }
            }));
            _readAt = DateTime.UtcNow;
        }
        finally { _reading = false; }
    }

    private void Show(MonthlyTokenTotals result)
    {
        ClaudeText = Display((result.Claude.Input, result.Claude.Output), "Local");
        CodexText = Display((result.Codex.Input, result.Codex.Output), "Local");
    }

    private static bool IsCacheError(Exception error) => error is SqliteException or IOException or UnauthorizedAccessException;

    private static string Display((long Input, long Output) t, string scope) => $"{scope} this month · {Format.Tokens(t.Input)} in · {Format.Tokens(t.Output)} out";
}
