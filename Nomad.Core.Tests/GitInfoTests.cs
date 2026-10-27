using Nomad.Core.Git;

namespace Nomad.Core.Tests;

public class GitInfoTests
{
    [Fact]
    public void StatusGivesTheBranchItsDistanceAndTheChangedFiles()
    {
        var status = GitInfo.ParseStatus("## main...origin/main [ahead 2, behind 1]\n M a.cs\n?? b.cs\n");

        Assert.Equal(("main", 2, 1, 2), status);
    }

    [Fact]
    public void StatusOfANewRepositoryAndADetachedHead()
    {
        Assert.Equal("dev", GitInfo.ParseStatus("## No commits yet on dev\n").Branch);
        Assert.Equal("detached", GitInfo.ParseStatus("## HEAD (no branch)\n").Branch);
        Assert.Equal(("feature/x", 0, 0, 0), GitInfo.ParseStatus("## feature/x\n"));
    }

    [Fact]
    public void LogLinesBecomeCommits()
    {
        var commits = GitInfo.ParseLog("abc1234\u001fFix the thing\u001fMartin\u001f1761555600\r\nbroken line\n");

        var commit = Assert.Single(commits);
        Assert.Equal("Fix the thing", commit.Subject);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761555600), commit.When);
    }
}
