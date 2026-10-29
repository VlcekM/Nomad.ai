using System.Text.Json;
using Nomad.Core.OpenCode;

namespace Nomad.Core.Tests;

public class OpenCodeCostTests
{
    private static readonly long Oct1 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static string Message(string role, double cost, long created, string provider = "anthropic", string model = "claude-opus-5-5") =>
        $$"""{ "info": { "role": "{{role}}", "cost": {{cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "providerID": "{{provider}}", "modelID": "{{model}}", "time": { "created": {{created}} } }, "parts": [] }""";

    [Fact]
    public void SumsThisMonthsAssistantCostsByModel()
    {
        var json = "[" + string.Join(",",
            Message("user", 0, Oct1 + 1),
            Message("assistant", 0.5, Oct1 - 1),                     // September: left out
            Message("assistant", 0.25, Oct1 + 10),
            Message("assistant", 0.125, Oct1 + 20),
            Message("assistant", 0.04, Oct1 + 30, model: "claude-haiku-5-5"),
            Message("assistant", 0, Oct1 + 40, "opencode", "big-pickle")) + "]";
        using var doc = JsonDocument.Parse(json);

        var sums = OpenCodeCost.SumMessages(doc.RootElement, Oct1);

        Assert.Equal(2, sums.Count);
        Assert.Equal(0.375m, sums["anthropic/claude-opus-5-5"]);
        Assert.Equal(0.04m, sums["anthropic/claude-haiku-5-5"]);
    }

    [Fact]
    public void ReportAddsSessionsUpBiggestFirst()
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var report = OpenCodeCost.Report(
            [
                new Dictionary<string, decimal> { ["anthropic/claude-opus-5-5"] = 1.5m, ["anthropic/claude-haiku-5-5"] = 0.1m },
                new Dictionary<string, decimal> { ["anthropic/claude-haiku-5-5"] = 0.2m, ["anthropic/claude-sonnet-5-5"] = 2m },
            ],
            start,
            start.AddDays(14));

        Assert.Equal(3.8m, report.TotalUsd);
        Assert.Equal(["anthropic/claude-sonnet-5-5", "anthropic/claude-opus-5-5", "anthropic/claude-haiku-5-5"], report.Lines.Select(l => l.Model));
        Assert.Equal(0.3m, report.Lines[2].Usd);
        Assert.Equal(start.AddMonths(1), report.MonthEnd);
    }
}
