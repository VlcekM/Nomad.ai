using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

public class SessionTitleTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void CustomTitleWinsAndCwdComesFromTheFirstRecordThatHasOne()
    {
        var file = FixturePath("session-titles.jsonl");
        var info = new FileInfo(file);

        var summary = SessionStore.ReadSummary(file, info.Length, info.LastWriteTimeUtc);

        Assert.NotNull(summary);
        Assert.Equal("Flaky login test", summary.Title);
        Assert.Equal(@"R:\Work\Demo", summary.WorkingDirectory);
        Assert.Equal("11111111-2222-3333-4444-555555555555", summary.SessionId);
    }

    [Fact]
    public void GeneratedTitleIsUsedWhenThereIsNoCustomTitle()
    {
        var lines = File.ReadAllLines(FixturePath("session-titles.jsonl")).Where(l => !l.Contains("\"custom-title\"")).ToArray();
        var file = Path.Combine(AppContext.BaseDirectory, $"title-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(file, lines);
        try
        {
            var info = new FileInfo(file);
            Assert.Equal("Fix flaky login test", SessionStore.ReadSummary(file, info.Length, info.LastWriteTimeUtc)?.Title);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FirstPromptSkipsMetaMarkupAndSidechains()
    {
        var records = File.ReadAllLines(FixturePath("session-titles.jsonl")).Select(TranscriptRecord.Parse).OfType<TranscriptRecord>();

        var first = records.Select(SessionTitles.PromptText).First(t => t is not null);

        Assert.StartsWith("Fix the   flaky", first);
        Assert.Equal("Fix the flaky login test in the auth module, it fails about…", SessionTitles.Shorten(first!));
    }

    [Fact]
    public void HistoryFollowsTheConversationChain()
    {
        var history = SessionStore.LoadHistory(FixturePath("session-titles.jsonl"));

        Assert.Collection(
            history.Items,
            i => Assert.IsType<HistoryUserMessage>(i),
            i => Assert.Equal("Looking at the test now.", Assert.IsType<HistoryAssistantText>(i).Text),
            i => Assert.Equal("Bash", Assert.IsType<HistoryToolCall>(i).Name),
            i => Assert.Equal("Passed: 12", Assert.IsType<HistoryToolResult>(i).Text));
        Assert.Equal("claude-haiku-5-5", history.Model);
    }
}
