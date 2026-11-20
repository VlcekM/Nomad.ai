using Nomad.App.Infrastructure;
using Nomad.Core.Antigravity;

namespace Nomad.App.ViewModels;

/// <summary>
/// Shows the signed-in Antigravity account's shared Gemini model limits.
/// </summary>
public sealed class AntigravityUsageViewModel : UsageSource
{
    /// <summary>
    /// Earliest time for the next regular refresh.
    /// </summary>
    private DateTimeOffset _refreshAfter;

    /// <summary>
    /// The time of the last good quota reading.
    /// </summary>
    private DateTimeOffset _shownAt;

    /// <summary>
    /// Whether the CLI is currently fetching quotas.
    /// </summary>
    private bool _fetching;

    /// <summary>
    /// Gives the popup its provider heading.
    /// </summary>
    public AntigravityUsageViewModel() => Title = "Antigravity usage limits";

    /// <summary>
    /// The usage card heading.
    /// </summary>
    public override string CardTitle => "Antigravity usage";

    /// <summary>
    /// The CLI is the source for this card.
    /// </summary>
    public override string? PageUrl => null;

    /// <summary>
    /// No external page is linked.
    /// </summary>
    public override string PageToolTip => "";

    /// <summary>
    /// Refreshes the limits at most once per minute during normal polling.
    /// </summary>
    public override async Task RefreshAsync(bool force = false)
    {
        if (_fetching || !force && DateTimeOffset.UtcNow < _refreshAfter) return;

        _fetching = true;
        try
        {
            await ReadAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            UnavailableText = ex.Message;
            IsUnavailable = !HasUsage;
            if (HasUsage)
            {
                StaleText = $"Couldn't refresh · as of {Format.When(_shownAt)}";
                StaleReason = ex.Message;
            }
        }
        finally
        {
            _refreshAfter = DateTimeOffset.UtcNow.AddMinutes(1);
            _fetching = false;
            IsLoading = false;
        }
    }

    /// <summary>
    /// Projects the quota windows into the existing usage rows and ring.
    /// </summary>
    private async Task ReadAsync()
    {
        var limits = await AntigravityUsage.FetchAsync();
        if (limits.Count == 0) throw new InvalidOperationException("Antigravity reported no Gemini plan limits.");

        _shownAt = DateTimeOffset.Now;
        StaleText = "";
        StaleReason = "";
        IsStaleReasonShown = false;
        Rows.Clear();
        foreach (var limit in limits) Rows.Add(UsageViewModel.Row(limit, _shownAt));

        var ring = limits.FirstOrDefault(limit => limit.Kind == "weekly_all") ?? limits[0];
        WeeklyFraction = ring.Percent / 100.0;
        WeeklyText = $"Antigravity · {ring.Label}: {ring.Percent}%";
        WeeklyResetsText = Format.ResetsAt(ring.ResetsAt);
        IsWeeklyHigh = ring.Severity != "normal";
        HasUsage = true;
        IsUnavailable = false;
    }
}
