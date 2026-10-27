using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

public class TranscriptIndexTests
{
    private const string Prompt = """{"type":"user","sessionId":"s","message":{"role":"user","content":[{"type":"text","text":"Find the flaky\nlogin test"}]}}""";
    private const string PlainPrompt = """{"type":"user","sessionId":"s","message":{"role":"user","content":"simpler stop"}}""";
    private const string Reminder = """{"type":"user","sessionId":"s","message":{"role":"user","content":[{"type":"text","text":"<system-reminder>secret words</system-reminder>"}]}}""";
    private const string ToolResult = """{"type":"user","sessionId":"s","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":[{"type":"text","text":"tool output words"}]}]}}""";
    private const string Reply = """{"type":"assistant","sessionId":"s","message":{"role":"assistant","content":[{"type":"text","text":"The login test waits on a \"timer\"."}]}}""";
    private const string Thinking = """{"type":"assistant","sessionId":"s","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hidden thoughts"}]}}""";
    private const string Sidechain = """{"type":"assistant","isSidechain":true,"sessionId":"s","message":{"role":"assistant","content":[{"type":"text","text":"subagent words"}]}}""";

    [Fact]
    public void IndexesPromptsAndRepliesOnly()
    {
        var file = Write(Prompt, PlainPrompt, Reminder, ToolResult, Reply, Thinking, Sidechain);
        try
        {
            var index = new TranscriptIndex();
            Assert.True(index.Update(file));
            var text = index.TextOf(file)!;

            Assert.Contains("flaky\nlogin", text);
            Assert.Contains("simpler stop", text);
            Assert.Contains("\"timer\"", text);
            Assert.DoesNotContain("secret", text);
            Assert.DoesNotContain("tool output", text);
            Assert.DoesNotContain("hidden", text);
            Assert.DoesNotContain("subagent", text);
            Assert.False(index.Update(file)); // Unchanged on disk.
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ReadsOnlyWhatWasAppended()
    {
        var file = Write(Prompt);
        try
        {
            var index = new TranscriptIndex();
            index.Update(file);
            File.AppendAllText(file, Reply + "\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));

            Assert.True(index.Update(file));
            Assert.Equal($"Find the flaky\nlogin test{TranscriptIndex.Separator}The login test waits on a \"timer\".", index.TextOf(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FindPrefersAMessageWithEveryWordAndCollapsesWhitespace()
    {
        var text = $"login page{TranscriptIndex.Separator}the flaky\n\nlogin test";
        var match = TranscriptIndex.Find(text, TranscriptIndex.Terms("LOGIN flaky"));

        Assert.NotNull(match);
        Assert.Equal("the flaky ", match.Before);
        Assert.Equal("login", match.Match);
        Assert.Equal(" test", match.After);
        Assert.Null(TranscriptIndex.Find(text, ["absent"]));
    }

    private static string Write(params string[] lines)
    {
        var file = Path.Combine(AppContext.BaseDirectory, $"index-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(file, string.Join("\n", lines) + "\n");
        return file;
    }
}
