using System.Globalization;
using System.Text.Json;
using Nomad.Core.OpenCode;

namespace Nomad.Core.Tests;

public class OpenCodeCostTests
{
    private static readonly long Oct1 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static string Message(string role, double cost, long created, string provider = "anthropic", string model = "claude-opus-5-5",
        long input = 0, long output = 0, long reasoning = 0, long read = 0, long write = 0) =>
        $$"""
        { "info": { "role": "{{role}}", "cost": {{cost.ToString(CultureInfo.InvariantCulture)}}, "providerID": "{{provider}}", "modelID": "{{model}}",
          "time": { "created": {{created}} },
          "tokens": { "input": {{input}}, "output": {{output}}, "reasoning": {{reasoning}}, "cache": { "read": {{read}}, "write": {{write}} } } },
          "parts": [] }
        """;

    [Fact]
    public void SumsThisMonthsPaidAssistantRepliesByModel()
    {
        var json = "[" + string.Join(",",
            Message("user", 0, Oct1 + 1),
            Message("assistant", 0.5, Oct1 - 1, input: 999),                                    // September: left out
            Message("assistant", 0.25, Oct1 + 10, input: 100, output: 20, reasoning: 5, read: 1000, write: 50),
            Message("assistant", 0.125, Oct1 + 20, input: 10, output: 30, read: 2000),
            Message("assistant", 0.04, Oct1 + 30, model: "claude-haiku-5-5", input: 7, output: 3),
            Message("assistant", 0, Oct1 + 40, "opencode", "big-pickle", input: 500)) + "]";  // free: left out
        using var doc = JsonDocument.Parse(json);

        var sums = OpenCodeCost.SumMessages(doc.RootElement, Oct1);

        Assert.Equal(2, sums.Count);
        Assert.Equal(new ModelUsage(0.375m, 110, 50, 5, 3000, 50), sums["anthropic/claude-opus-5-5"]);
        Assert.Equal(new ModelUsage(0.04m, 7, 3), sums["anthropic/claude-haiku-5-5"]);
    }

    [Fact]
    public void ReportAddsSessionsUpBiggestFirst()
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var report = OpenCodeCost.Report(
            [
                new Dictionary<string, ModelUsage> { ["anthropic/claude-opus-5-5"] = new(1.5m, 10), ["anthropic/claude-haiku-5-5"] = new(0.1m, Output: 4) },
                new Dictionary<string, ModelUsage> { ["anthropic/claude-haiku-5-5"] = new(0.2m, Output: 6), ["anthropic/claude-sonnet-5-5"] = new(2m) },
            ],
            start,
            start.AddDays(14));

        Assert.Equal(3.8m, report.TotalUsd);
        Assert.Equal(["anthropic/claude-sonnet-5-5", "anthropic/claude-opus-5-5", "anthropic/claude-haiku-5-5"], report.Lines.Select(l => l.Model));
        Assert.Equal(new ModelUsage(0.3m, Output: 10), report.Lines[2].Usage);
        Assert.Equal(start.AddMonths(1), report.MonthEnd);
    }
}
