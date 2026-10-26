using Nomad.Core.Usage;

namespace Nomad.Core.Tests;

/// <summary>The plan usage response, trimmed from a real <c>/api/oauth/usage</c> answer (see Fixtures).</summary>
public class PlanUsageTests
{
    [Fact]
    public void ParsesTheLimitsList()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "usage.json"));

        var usage = PlanUsageClient.Parse(json, "Max (5x)");

        Assert.NotNull(usage);
        Assert.Equal(["Session limit", "Weekly · all models", "Weekly · Fable"], usage.Limits.Select(l => l.Label));
        Assert.Equal([23, 56, 27], usage.Limits.Select(l => l.Percent));
        Assert.Equal(23, usage.Session!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 23, 10, 0, TimeSpan.Zero), usage.Session.ResetsAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("max", "default_claude_max_5x", "Max (5x)")]
    [InlineData("max", "default_claude_max_20x", "Max (20x)")]
    [InlineData("pro", "default_claude_ai", "Pro")]
    [InlineData(null, null, null)]
    public void NamesThePlan(string? subscription, string? tier, string? expected) =>
        Assert.Equal(expected, PlanUsageClient.PlanName(subscription, tier));
}
