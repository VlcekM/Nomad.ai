using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

/// <summary>
/// A chat's own folder is where it was started, not wherever its shell had moved. The paths are Windows paths, so
/// elsewhere the tests return at once.
/// </summary>
public sealed class HomeFolderTests
{
    private const string Transcript = @"C:\Users\u\.claude\projects\C--Work-Game\a.jsonl";

    [Theory]
    [InlineData(@"C:\Work\Game", @"C:\Work\Game")]
    [InlineData(@"C:\Work\Game\Client", @"C:\Work\Game")]
    [InlineData(@"C:\Work\Game\mods\one\tools\probe", @"C:\Work\Game")]
    [InlineData(@"C:\Work\Game\", @"C:\Work\Game\")]
    [InlineData(@"c:\work\game\notes", @"c:\work\game")]
    public void IsTheRecordedFolderOrTheParentTheTranscriptIsFiledUnder(string cwd, string home)
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(home, SessionStore.HomeFolder(Transcript, cwd));
    }

    [Fact]
    public void IsNullWhenNoFolderMatches()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Null(SessionStore.HomeFolder(Transcript, @"D:\Elsewhere\sub"));
        Assert.Null(SessionStore.HomeFolder(Transcript, null));
    }

    [Fact]
    public void ASummaryKeepsTheStartFolderWhenTheFirstRecordsWereMadeInASubfolder()
    {
        if (!OperatingSystem.IsWindows()) return;

        var project = Path.Combine(AppContext.BaseDirectory, $"home-{Guid.NewGuid():N}", "Q--Proj");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, "11111111-2222-3333-4444-555555555555.jsonl");
        File.WriteAllText(file,
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Fix the parser"}]},"cwd":"Q:\\Proj\\tools\\probe","sessionId":"11111111-2222-3333-4444-555555555555","uuid":"u1"}""" + "\n");
        try
        {
            var info = new FileInfo(file);

            var summary = SessionStore.ReadSummary(file, info.Length, info.LastWriteTimeUtc);

            Assert.Equal(@"Q:\Proj", summary?.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(project)!, recursive: true);
        }
    }
}
