using Nomad.Core.Hub;

namespace Nomad.Core.Tests;

public sealed class HubSyncTests
{
    [Theory]
    [InlineData("git@github.com:VlcekM/Nomad.ai.git", "github.com/vlcekm/nomad.ai")]
    [InlineData("https://github.com/VlcekM/Nomad.ai", "github.com/vlcekm/nomad.ai")]
    [InlineData("https://me@github.com/VlcekM/Nomad.ai.git/", "github.com/vlcekm/nomad.ai")]
    [InlineData("ssh://git@gitlab.example.com/group/app.git", "gitlab.example.com/group/app")]
    public void RemotesNormaliseToOneKey(string url, string key) => Assert.Equal(key, ProjectKeys.Normalise(url));

    [Fact]
    public void CodexUsageIsPerCallWithCachedInputSplitOff()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, [
                """{"timestamp":"2026-10-30T09:00:00Z","ordinal":1,"type":"turn_context","payload":{"model":"gpt-6.1-sol"}}""",
                """{"timestamp":"2026-10-30T09:00:01Z","ordinal":2,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":120},"last_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"reasoning_output_tokens":5}}}}""",
                // A repeat (the running total didn't move) is not a new call.
                """{"timestamp":"2026-10-30T09:00:02Z","ordinal":3,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":120},"last_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20}}}}""",
                """{"timestamp":"2026-10-30T09:00:03Z","ordinal":4,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":150},"last_token_usage":{"input_tokens":25,"cached_input_tokens":0,"output_tokens":5}}}}""",
            ]);
            var records = UsageExtractor.Codex("t1", file);
            Assert.Equal(2, records.Count);
            Assert.Equal(("o2", "gpt-6.1-sol", 40L, 60L, 20L, 5L), (records[0].MessageId, records[0].Model, records[0].Input, records[0].CacheRead, records[0].Output, records[0].Reasoning));
            Assert.Equal("o4", records[1].MessageId);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ClaudeUsageCountsEachMessageOnce()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, [
                """{"type":"assistant","timestamp":"2026-10-30T09:00:00Z","message":{"id":"msg_1","model":"claude-opus-5-5","usage":{"input_tokens":3,"output_tokens":1,"cache_read_input_tokens":50,"cache_creation_input_tokens":7}}}""",
                """{"type":"assistant","timestamp":"2026-10-30T09:00:01Z","message":{"id":"msg_1","model":"claude-opus-5-5","usage":{"input_tokens":3,"output_tokens":40,"cache_read_input_tokens":50,"cache_creation_input_tokens":7}}}""",
                """{"type":"assistant","timestamp":"2026-10-30T09:00:02Z","message":{"id":"msg_2","model":"<synthetic>","usage":{"input_tokens":0,"output_tokens":0}}}""",
            ]);
            var record = Assert.Single(UsageExtractor.Claude("s1", file));
            Assert.Equal((3L, 40L, 50L, 7L), (record.Input, record.Output, record.CacheRead, record.CacheWrite));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
