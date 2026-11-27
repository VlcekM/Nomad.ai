using Nomad.Core.Usage;

namespace Nomad.Core.Tests;

public class LocalTokenUsageTests
{
    [Fact]
    public void CacheResumesCountersAndReconcilesChangedLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var claude = Path.Combine(root, "claude");
        var codex = Path.Combine(root, "codex");
        Directory.CreateDirectory(claude);
        Directory.CreateDirectory(Path.Combine(codex, "sessions"));
        var since = DateTimeOffset.UtcNow.Date.AddDays(-1);
        var at = DateTimeOffset.UtcNow.ToString("O");
        var claudeFile = Path.Combine(claude, "one.jsonl");
        var duplicate = Path.Combine(claude, "two.jsonl");
        var codexFile = Path.Combine(codex, "sessions", "one.jsonl");
        var database = Path.Combine(root, "usage.db");
        try
        {
            string Message(int input, string content = "") =>
                $"{{\"type\":\"assistant\",\"timestamp\":\"{at}\",\"message\":{{\"content\":\"{content}\",\"id\":\"one\",\"usage\":{{\"input_tokens\":{input},\"output_tokens\":1}}}}}}";
            string Count(int input) =>
                $"{{\"type\":\"event_msg\",\"timestamp\":\"{at}\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"input_tokens\":{input}}}}}}}}}";

            var cache = new LocalTokenCache(database, claude, codex);
            Assert.Null(cache.ReadCached(since));
            File.WriteAllLines(claudeFile, [Message(8, new string('x', 150_000) + "\\\"quoted\\\"")]);
            File.WriteAllLines(duplicate, [Message(5)]);
            File.WriteAllLines(codexFile, [Count(50)]);
            var initial = new MonthlyTokenTotals(new(8, 1), new(50, 0));
            Assert.Equal(initial, cache.Refresh(since));
            Assert.Equal(initial, new LocalTokenCache(database, claude, codex).ReadCached(since));

            File.AppendAllLines(claudeFile, [Message(10)]);
            File.AppendAllLines(codexFile, [Count(60)]);
            Assert.Equal(new MonthlyTokenTotals(new(10, 1), new(60, 0)), cache.Refresh(since));

            File.WriteAllText(codexFile, Count(7));
            File.Delete(claudeFile);
            Assert.Equal(new MonthlyTokenTotals(new(5, 1), new(7, 0)), cache.Refresh(since));
            File.AppendAllText(codexFile, "\n" + Count(9) + "\n");
            Assert.Equal(new MonthlyTokenTotals(new(5, 1), new(9, 0)), cache.Refresh(since));
            Assert.Null(cache.ReadCached(since.AddDays(1)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ClaudeDeduplicatesMessagesAndCountsCachedInput()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllLines(Path.Combine(root, "chat.jsonl"), [
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-01T10:00:00Z\",\"message\":{\"id\":\"one\",\"usage\":{\"input_tokens\":2,\"cache_read_input_tokens\":10,\"cache_creation_input_tokens\":5,\"output_tokens\":3}}}",
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-01T10:00:01Z\",\"message\":{\"id\":\"one\",\"usage\":{\"input_tokens\":2,\"cache_read_input_tokens\":10,\"cache_creation_input_tokens\":5,\"output_tokens\":7}}}",
                "{\"type\":\"assistant\",\"timestamp\":\"2026-09-01T10:00:00Z\",\"message\":{\"id\":\"old\",\"usage\":{\"input_tokens\":999}}}",
                "{incomplete"
            ]);
            Assert.Equal(new LocalTokens(17, 7), LocalTokenUsage.Claude(root, DateTimeOffset.Parse("2026-10-01T00:00:00Z")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CodexCountsOnlyChangesWithinMonthAndHandlesCounterReset()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var folder = Path.Combine(root, "sessions");
        Directory.CreateDirectory(folder);
        try
        {
            static string Event(string at, int input, int output) => $"{{\"type\":\"event_msg\",\"timestamp\":\"{at}\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"input_tokens\":{input},\"output_tokens\":{output}}}}}}}}}";
            File.WriteAllLines(Path.Combine(folder, "chat.jsonl"), [Event("2026-09-30T23:00:00Z", 100, 10), Event("2026-10-01T01:00:00Z", 150, 15), Event("2026-10-01T02:00:00Z", 150, 15), Event("2026-10-02T00:00:00Z", 20, 2)]);
            Assert.Equal(new LocalTokens(70, 7), LocalTokenUsage.Codex(root, DateTimeOffset.Parse("2026-10-01T00:00:00Z")));
        }
        finally { Directory.Delete(root, true); }
    }
}
