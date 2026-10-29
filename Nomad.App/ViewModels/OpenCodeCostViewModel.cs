using System.Globalization;
using Nomad.App.Infrastructure;
using Nomad.Core.OpenCode;

namespace Nomad.App.ViewModels;

/// <summary>
/// OpenCode sessions' usage: what OpenCode spent so far this month, from the cost it records with each reply, with a
/// row per model sized by its share and a month-end forecast. Read when the popup or the side panel opens, after
/// turns and every five minutes, at most every half minute.
/// </summary>
public sealed partial class OpenCodeCostViewModel : UsageSource
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    private DateTime _fetchedUtc = DateTime.MinValue;
    private DateTimeOffset _shownAt;
    private bool _fetching;

    public OpenCodeCostViewModel()
    {
        Title = "OpenCode cost this month";
    }

    public override string CardTitle => "OpenCode cost";

    public override string? PageUrl => null;

    public override string PageToolTip => "";

    public override async Task RefreshAsync(bool force = false)
    {
        if (_fetching || (!force && DateTime.UtcNow - _fetchedUtc < MinInterval))
        {
            return;
        }

        _fetching = true;
        try
        {
            var server = await OpenCodeServer.GetAsync();
            var report = await OpenCodeCost.MonthAsync(server, DateTimeOffset.Now);
            _fetchedUtc = DateTime.UtcNow;
            Show(report);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _fetchedUtc = DateTime.UtcNow;
            var reason = $"Couldn't read OpenCode's costs: {ex.Message}";
            if (HasUsage)
            {
                // The last total stands, marked stale, with why.
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

    private void Show(CostReport report)
    {
        _shownAt = report.At;
        var month = report.MonthStart.ToString("MMMM", CultureInfo.InvariantCulture);
        Title = $"OpenCode cost · {month}";
        StaleText = "";
        StaleReason = "";
        IsStaleReasonShown = false;

        Rows.Clear();
        var total = report.TotalUsd;
        var elapsed = (report.At - report.MonthStart) / (report.MonthEnd - report.MonthStart);
        var forecast = elapsed >= 0.05 && total > 0 ? total / (decimal)elapsed : (decimal?)null;

        // The month so far: its bar is how far into the month it is; the forecast carries the pace on to its end.
        Rows.Add(new UsageRow(
            "This month",
            $"since 1 {report.MonthStart:MMM}",
            Money(total),
            Math.Clamp(elapsed, 0, 1),
            false,
            ForecastText: forecast is { } f ? $"At this rate, about {Money(f)} by the end of {month}"
                : total == 0 ? "Nothing spent yet this month"
                : ""));

        // Each model's share of it, the biggest first; the long tail folded into one row.
        const int shown = 5;
        foreach (var line in report.Lines.Take(report.Lines.Count > shown ? shown - 1 : shown))
        {
            Rows.Add(new UsageRow(Catalog.ModelName(line.Model), "", Money(line.Usd), total > 0 ? (double)(line.Usd / total) : 0, false));
        }

        if (report.Lines.Count > shown)
        {
            var rest = report.Lines.Skip(shown - 1).Sum(l => l.Usd);
            Rows.Add(new UsageRow($"{report.Lines.Count - shown + 1} more models", "", Money(rest), total > 0 ? (double)(rest / total) : 0, false));
        }

        ChipText = Money(total);
        WeeklyText = $"OpenCode cost this month: {Money(total)}";
        WeeklyResetsText = forecast is { } g ? $"About {Money(g)} by the end of {month}" : "";
        HasUsage = true;
        IsUnavailable = false;
    }

    /// <summary>"$12.34"; large sums drop the cents ("$1,234").</summary>
    public static string Money(decimal usd) =>
        "$" + (usd >= 1000 ? usd.ToString("#,0", CultureInfo.InvariantCulture) : usd.ToString("#,0.00", CultureInfo.InvariantCulture));
}
