using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.App.Infrastructure;
using Nomad.Core.Usage;

namespace Nomad.App.ViewModels;

/// <summary>
/// A row of the usage popup: label, reset time, percent and a bar. Weekly limits also carry the even pace (where
/// using the week up evenly, around the clock, would be by now: -1 when not known) and a forecast line.
/// </summary>
public sealed record UsageRow(
    string Label,
    string ResetsText,
    string PercentText,
    double Fraction,
    bool IsHigh,
    double PaceFraction = -1,
    string PaceToolTip = "",
    string ForecastText = "",
    bool IsForecastShort = false)
{
    public bool HasForecast => ForecastText.Length > 0;

    public bool HasPace => PaceFraction >= 0;
}

/// <summary>
/// Plan usage limits for the status-row ring's popup. Fetched at start, after every turn, when the
/// popup opens and every five minutes, at most once a minute.
/// </summary>
public sealed partial class UsageViewModel : ObservableObject
{
    public const string UsagePageUrl = "https://claude.ai/settings/usage";

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private DateTime _fetchedUtc = DateTime.MinValue;
    private bool _fetching;

    public ObservableCollection<UsageRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = "Plan usage limits";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnavailable))]
    public partial bool HasUsage { get; set; }

    /// <summary>"Couldn't refresh · as of 12:40" when the limits shown are the last ones read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStale))]
    public partial string StaleText { get; set; } = "";

    public bool IsStale => StaleText.Length > 0;

    /// <summary>How much of the weekly limit is used (the ring in the status row).</summary>
    [ObservableProperty]
    public partial double WeeklyFraction { get; set; }

    /// <summary>"Weekly · all models: 68%" (the ring's tooltip).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWeekly))]
    public partial string WeeklyText { get; set; } = "";

    public bool HasWeekly => WeeklyText.Length > 0;

    /// <summary>"Resets Fri 9:00 AM".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWeeklyResets))]
    public partial string WeeklyResetsText { get; set; } = "";

    public bool HasWeeklyResets => WeeklyResetsText.Length > 0;

    [ObservableProperty]
    public partial bool IsWeeklyHigh { get; set; }

    /// <summary>Fetched at least once and got nothing (API-key sign-in, offline, expired sign-in).</summary>
    [ObservableProperty]
    public partial bool IsUnavailable { get; set; }

    /// <summary>The first read hasn't come back yet (the ring and the usage cards show placeholders).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    public async Task RefreshAsync(bool force = false)
    {
        if (_fetching || (!force && DateTime.UtcNow - _fetchedUtc < MinInterval))
        {
            return;
        }

        _fetching = true;
        try
        {
            var usage = await PlanUsageClient.FetchAsync();
            _fetchedUtc = DateTime.UtcNow;
            if (usage is null)
            {
                IsUnavailable = !HasUsage;
                return;
            }

            Title = usage.PlanName is { } plan ? $"Plan usage limits · {plan}" : "Plan usage limits";
            StaleText = usage.StaleSince is { } since ? $"Couldn't refresh · as of {Format.When(since)}" : "";
            Rows.Clear();
            foreach (var limit in usage.Limits)
            {
                Rows.Add(Row(limit, DateTimeOffset.Now));
            }

            // The status-row ring: the weekly all-models limit (the session one when there is no weekly).
            var weekly = usage.Limits.FirstOrDefault(l => l.Kind == "weekly_all") ?? usage.Session;
            WeeklyFraction = weekly is null ? 0 : Math.Clamp(weekly.Percent / 100.0, 0, 1);
            WeeklyText = weekly is null ? "" : $"{weekly.Label}: {weekly.Percent}%";
            WeeklyResetsText = weekly?.ResetsAt is { } at ? Format.ResetsAt(at) : "";
            IsWeeklyHigh = weekly is { Severity: not "normal" };

            HasUsage = Rows.Count > 0;
            IsUnavailable = !HasUsage;
        }
        finally
        {
            _fetching = false;
            IsLoading = false;
        }
    }

    public static UsageRow Row(PlanLimit limit, DateTimeOffset now)
    {
        var row = new UsageRow(
            limit.Label,
            Format.ResetsAt(limit.ResetsAt),
            $"{limit.Percent}%",
            Math.Clamp(limit.Percent / 100.0, 0, 1),
            limit.Severity != "normal");
        if (PlanPace.For(limit, now) is not { } pace)
        {
            return row;
        }

        row = row with
        {
            PaceFraction = pace.Fraction,
            PaceToolTip = $"Even pace: {pace.Fraction * 100:0}% by now, using the week up evenly, around the clock",
        };
        if (!pace.CanForecast)
        {
            return row;
        }

        if (limit.Percent <= 0)
        {
            return row with { ForecastText = "Nothing used yet this week" };
        }

        if (limit.Percent >= 100)
        {
            return row with { ForecastText = "Used up until the reset", IsForecastShort = true };
        }

        return pace.RunsOut is { } runsOut && limit.ResetsAt is { } resets
            ? row with
            {
                ForecastText = $"At this rate, runs out {Format.When(runsOut)} ({Format.Span(resets - runsOut)} early)",
                IsForecastShort = true,
            }
            : row with { ForecastText = $"At this rate, about {pace.ByReset:0}% by the reset" };
    }
}
