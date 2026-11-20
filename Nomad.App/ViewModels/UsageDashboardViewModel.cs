using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.Core.Agents;

namespace Nomad.App.ViewModels;

/// <summary>
/// A provider available for a dashboard slot.
/// </summary>
public sealed record UsageDashboardCard(string Id, string Name, UsageSource Source, LocalTokenUsageViewModel LocalTokens)
{
    /// <summary>
    /// Shows the local Claude token footer on Claude's card.
    /// </summary>
    public bool ShowsClaudeTokens => Id == AgentBackends.ClaudeCode;

    /// <summary>
    /// Shows the local Codex token footer on Codex's card.
    /// </summary>
    public bool ShowsCodexTokens => Id == AgentBackends.Codex;

    /// <summary>
    /// Whether the provider has a local token footer.
    /// </summary>
    public bool ShowsLocalTokens => ShowsClaudeTokens || ShowsCodexTokens;

    /// <summary>
    /// Displays OpenCode's expandable cost breakdown.
    /// </summary>
    public bool ShowsCost => Id == AgentBackends.OpenCode;

    /// <summary>
    /// Displays the provider's quota rows.
    /// </summary>
    public bool ShowsLimits => !ShowsCost;
}

/// <summary>
/// Holds the three visible cards and a draft edited by the dashboard dialog.
/// </summary>
public sealed partial class UsageDashboardViewModel : ObservableObject
{
    /// <summary>
    /// The providers available in each slot.
    /// </summary>
    public IReadOnlyList<UsageDashboardCard> Options { get; }

    /// <summary>
    /// The three applied cards, in display order.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<UsageDashboardCard> Cards { get; private set; }

    /// <summary>
    /// The draft left card.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial UsageDashboardCard? Left { get; set; }

    /// <summary>
    /// The draft middle card.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial UsageDashboardCard? Middle { get; set; }

    /// <summary>
    /// The draft right card.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial UsageDashboardCard? Right { get; set; }

    /// <summary>
    /// Allows a layout with three distinct providers.
    /// </summary>
    public bool CanApply => Left is not null && Middle is not null && Right is not null
        && Left != Middle && Left != Right && Middle != Right;

    /// <summary>
    /// Restores the saved cards and fills missing slots with default providers.
    /// </summary>
    public UsageDashboardViewModel(MainViewModel main)
    {
        var tokens = main.LocalTokens;
        Options =
        [
            new(AgentBackends.ClaudeCode, "Claude", main.Usage, tokens),
            new(AgentBackends.Codex, "Codex", main.CodexUsage, tokens),
            new(AgentBackends.Antigravity, "Antigravity", main.AntigravityUsage, tokens),
            new(AgentBackends.OpenCode, "OpenCode cost", main.OpenCodeCost, tokens),
        ];

        Cards = (main.Settings.NewSessionUsageCards ?? [])
            .Select(id => Options.FirstOrDefault(option => option.Id == id))
            .OfType<UsageDashboardCard>()
            .Concat(Options)
            .Distinct()
            .Take(3)
            .ToArray();
    }

    /// <summary>
    /// Opens the dialog with the currently displayed cards.
    /// </summary>
    public void BeginEdit()
    {
        Left = Cards[0];
        Middle = Cards[1];
        Right = Cards[2];
    }

    /// <summary>
    /// Publishes the draft and returns the IDs for settings storage.
    /// </summary>
    public List<string> Apply()
    {
        if (CanApply) Cards = [Left!, Middle!, Right!];

        return [.. Cards.Select(card => card.Id)];
    }
}
