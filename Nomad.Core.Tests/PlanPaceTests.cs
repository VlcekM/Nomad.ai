using Nomad.Core.Usage;

namespace Nomad.Core.Tests;

public class PlanPaceTests
{
    private static readonly DateTimeOffset Resets = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static PlanLimit Weekly(int percent) => new("weekly_all", "Weekly · all models", percent, "normal", Resets);

    [Fact]
    public void HalfwayThroughTheWeekThePaceIsHalf()
    {
        var pace = PlanPace.For(Weekly(30), Resets - TimeSpan.FromDays(3.5));

        Assert.NotNull(pace);
        Assert.Equal(0.5, pace.Fraction, 3);
        Assert.Null(pace.RunsOut);
        Assert.Equal(60, pace.ByReset, 3);
    }

    [Fact]
    public void AheadOfPaceRunsOutBeforeTheReset()
    {
        // 2 days in, 40 % used: 100 % after 5 days, 2 days before the reset.
        var pace = PlanPace.For(Weekly(40), Resets - TimeSpan.FromDays(5));

        Assert.Equal(Resets - TimeSpan.FromDays(2), pace?.RunsOut);
    }

    [Fact]
    public void NoForecastEarlyInTheWeekOrForOtherLimits()
    {
        Assert.False(PlanPace.For(Weekly(10), Resets - TimeSpan.FromDays(6.9))?.CanForecast);
        Assert.Null(PlanPace.For(new PlanLimit("session", "Session limit", 20, "normal", Resets), Resets - TimeSpan.FromHours(2)));
        Assert.Null(PlanPace.For(Weekly(10), Resets - TimeSpan.FromDays(8)));
    }
}
