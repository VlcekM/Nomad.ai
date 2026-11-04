using Nomad.Core.Git;
using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

public class WorktreeMoveTests
{
    private const string Repo = @"R:\Work\Repo";
    private const string Worktree = @"R:\Work\Repo\.claude\worktrees\overlay";

    [Fact]
    public void MovingIntoAndOutOfAWorktreeIsNoted()
    {
        Assert.Equal("Moved to worktree overlay", GitInfo.WorktreeMoveNotice(Repo, Worktree + @"\Admin"));
        Assert.Equal("Left worktree overlay", GitInfo.WorktreeMoveNotice(Worktree, Repo));
        Assert.Equal("Moved to worktree other", GitInfo.WorktreeMoveNotice(Worktree, @"R:\Work\Repo\.claude\worktrees\other"));
    }

    [Fact]
    public void ACdWithinTheSameCheckoutIsNot()
    {
        Assert.Null(GitInfo.WorktreeMoveNotice(Repo, Repo + @"\src"));
        Assert.Null(GitInfo.WorktreeMoveNotice(Worktree, Worktree + @"\src"));
        Assert.Equal("overlay", GitInfo.WorktreeName(Worktree + @"\src"));
        Assert.Null(GitInfo.WorktreeName(Repo));
    }

    [Fact]
    public void TheHistoryShowsTheMoveWhereTheCliRecordedIt()
    {
        var history = SessionHistory.FromRecords([.. Lines().Select(l => TranscriptRecord.Parse(l)!)], truncated: false, "s");

        Assert.Equal(Repo, history.WorkingDirectory);
        Assert.Equal(["go", "Moved to worktree overlay", "done"], history.Items.Select(Text));
    }

    [Fact]
    public void TheCurrentDirectoryIsTheNewestRecords()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, [.. Lines(), """{"type":"last-prompt","lastPrompt":"go","sessionId":"s"}"""]);

            Assert.Equal(Worktree, SessionStore.CurrentDirectory(file));

            // The sidebar's summary keeps where it started, and learns where it works now.
            var summary = new SessionStore().GetSummary(file)!;
            Assert.Equal(Repo, summary.WorkingDirectory);
            Assert.Equal(Worktree, summary.WorkFolder);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string Text(HistoryItem item) => item switch
    {
        HistoryUserMessage user => user.Text,
        HistoryNotice notice => notice.Text,
        HistoryAssistantText text => text.Text,
        _ => item.GetType().Name,
    };

    private static string[] Lines()
    {
        var repo = Repo.Replace(@"\", @"\\");
        var worktree = Worktree.Replace(@"\", @"\\");
        return
        [
            $$"""{"parentUuid":null,"type":"user","message":{"role":"user","content":"go"},"uuid":"u1","cwd":"{{repo}}","sessionId":"s"}""",
            $$"""{"parentUuid":"u1","type":"attachment","attachment":{"type":"environment","snapshot":{"workingDirectory":"{{worktree}}","isWorktree":true},"changes":[{"field":"workingDirectory","from":"{{repo}}"},{"field":"isWorktree"}]},"uuid":"t1","cwd":"{{worktree}}","sessionId":"s"}""",
            $$"""{"parentUuid":"t1","type":"assistant","message":{"id":"m1","role":"assistant","content":[{"type":"text","text":"done"}]},"uuid":"a1","cwd":"{{worktree}}","sessionId":"s"}""",
        ];
    }
}
