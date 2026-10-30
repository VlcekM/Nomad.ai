using Nomad.Core.Usage;

namespace Nomad.Core.Tests;

public class LocalTokenUsageTests
{
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
