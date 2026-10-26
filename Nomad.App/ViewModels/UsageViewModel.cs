using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.App.Infrastructure;
using Nomad.Core.Usage;

namespace Nomad.App.ViewModels;

/// <summary>A row of the usage popup: label, reset time, percent and a bar.</summary>
public sealed record UsageRow(string Label, string ResetsText, string PercentText, double Fraction, bool IsHigh);

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

    /// <summary>Fetched at least once and got nothing (API-key sign-in, offline, expired sign-in).</summary>
    [ObservableProperty]
    public partial bool IsUnavailable { get; set; }

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
            Rows.Clear();
            foreach (var limit in usage.Limits)
            {
                Rows.Add(new UsageRow(
                    limit.Label,
                    Format.ResetsAt(limit.ResetsAt),
                    $"{limit.Percent}%",
                    Math.Clamp(limit.Percent / 100.0, 0, 1),
                    limit.Severity != "normal"));
            }

            HasUsage = Rows.Count > 0;
            IsUnavailable = !HasUsage;
        }
        finally
        {
            _fetching = false;
        }
    }
}
