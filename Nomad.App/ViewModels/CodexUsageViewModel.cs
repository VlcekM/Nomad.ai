using Avalonia.Threading;
using Nomad.App.Infrastructure;
using Nomad.Core.Codex;
using Nomad.Core.Usage;

namespace Nomad.App.ViewModels;

/// <summary>
/// Codex sessions' usage: the ChatGPT plan's Codex limits (the 5-hour window and the weekly one), as Claude's plan
/// limits are shown, pace and forecast included. Read when the popup or the side panel opens, after turns and every
/// five minutes, at most every half minute; the server's own updates (after each model call) come in between.
/// </summary>
public sealed partial class CodexUsageViewModel : UsageSource
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    private DateTime _fetchedUtc = DateTime.MinValue;
    private DateTimeOffset _shownAt;
    private bool _fetching;
    private CodexServer? _listening;
    private string? _plan;

    public CodexUsageViewModel()
    {
        Title = "Codex usage limits";
    }

    public override string CardTitle => "Codex usage";

    /// <summary>Where the Codex CLI itself sends people to see their usage.</summary>
    public const string UsagePageUrl = "https://chatgpt.com/settings/usage";

    public override string? PageUrl => UsagePageUrl;

    public override string PageToolTip => "Usage on chatgpt.com";

    public override async Task RefreshAsync(bool force = false)
    {
        if (_fetching || (!force && DateTime.UtcNow - _fetchedUtc < MinInterval))
        {
            return;
        }

        _fetching = true;
        try
        {
            if (CodexServer.Current is null && !(CodexServer.IsInstalled && CodexServer.HasData))
            {
                UnavailableText = "Codex usage needs Codex, signed in with ChatGPT (codex login).";
                IsUnavailable = !HasUsage;
                return;
            }

            var server = await CodexServer.GetAsync();
            Listen(server);
            var (type, _, plan) = await CodexClient.AccountAsync(server);
            _plan = plan;
            _fetchedUtc = DateTime.UtcNow;
            if (type != "chatgpt")
            {
                UnavailableText = type is null
                    ? "Codex is signed out. Run “codex login” in a terminal."
                    : "Codex runs on an API key: it has no plan limits.";
                IsUnavailable = !HasUsage;
                return;
            }

            Show(await CodexClient.RateLimitsAsync(server));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _fetchedUtc = DateTime.UtcNow;
            var reason = $"Couldn't read Codex's limits: {ex.Message}";
            if (HasUsage)
            {
                StaleText = $"Couldn't refresh · as of {Format.When(_shownAt)}";
                StaleReason = reason;
            }
            else
            {
                UnavailableText = reason;
                IsUnavailable = true;
            }
        }
        finally
        {
            _fetching = false;
            IsLoading = false;
        }
    }

    /// <summary>The server tells every client the new limits after each model call: they show at once.</summary>
    private void Listen(CodexServer server)
    {
        if (ReferenceEquals(_listening, server))
        {
            return;
        }

        _listening = server;
        server.Notification += (method, parameters) =>
        {
            if (method == "account/rateLimits/updated" && CodexClient.RateLimits(parameters.TryGetProperty("rateLimits", out var r) ? r : default) is { } limits)
            {
                Dispatcher.UIThread.Post(() => Show(limits));
            }
        };
    }

    private void Show(CodexRateLimits? limits)
    {
        var now = DateTimeOffset.Now;
        _shownAt = now;
        var plan = limits?.PlanType ?? _plan;
        Title = plan is { Length: > 0 } ? $"Codex usage limits · {PlanName(plan)}" : "Codex usage limits";
        StaleText = "";
        StaleReason = "";
        IsStaleReasonShown = false;

        Rows.Clear();
        var session = Limit("session", limits?.Primary);
        var weekly = Limit("weekly_all", limits?.Secondary);
        foreach (var limit in new[] { session, weekly }.OfType<PlanLimit>())
        {
            Rows.Add(UsageViewModel.Row(limit, now));
        }

        // The status-row ring: the weekly limit (the 5-hour one when there is no weekly).
        var ring = weekly ?? session;
        WeeklyFraction = ring is null ? 0 : Math.Clamp(ring.Percent / 100.0, 0, 1);
        WeeklyText = ring is null ? "" : $"Codex · {ring.Label}: {ring.Percent}%";
        WeeklyResetsText = ring?.ResetsAt is { } at ? Format.ResetsAt(at) : "";
        IsWeeklyHigh = ring is { Severity: not "normal" };
        HasUsage = Rows.Count > 0;
        IsUnavailable = !HasUsage;
        if (!HasUsage)
        {
            UnavailableText = "Codex reported no limits for this account.";
        }
    }

    /// <summary>A Codex window as a plan limit: labelled by its length, "high" from 80 %.</summary>
    private static PlanLimit? Limit(string kind, CodexLimitWindow? window)
    {
        if (window is null)
        {
            return null;
        }

        // A week-long window paces as Claude's weekly limit does; a shorter one is the session window.
        var minutes = window.WindowMinutes ?? 0;
        var isWeek = minutes >= 7 * 24 * 60 - 60;
        var label = minutes switch
        {
            0 => kind == "session" ? "Current session" : "Weekly limit",
            _ when isWeek => "Weekly limit",
            >= 60 when minutes % 60 == 0 => $"{minutes / 60}-hour limit",
            _ => $"{minutes}-minute limit",
        };
        var percent = (int)Math.Round(Math.Clamp(window.UsedPercent, 0, 100));
        return new PlanLimit(
            isWeek ? "weekly_all" : "session",
            label,
            percent,
            percent >= 80 ? "warning" : "normal",
            window.ResetsAtUtc is { } resets ? new DateTimeOffset(resets, TimeSpan.Zero).ToLocalTime() : null);
    }

    private static string PlanName(string plan) => plan switch
    {
        "plus" => "Plus",
        "pro" => "Pro",
        "prolite" => "Pro Lite",
        "promax" => "Pro Max",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Edu",
        "free" => "Free",
        "go" => "Go",
        _ => Format.Capitalize(plan.Replace('_', ' ')),
    };
}
