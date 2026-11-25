using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

public class RenameTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void ACustomTitleRecordRenamesTheSessionEvenAfterALineWithoutNewline()
    {
        var file = Path.Combine(AppContext.BaseDirectory, $"rename-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(file, File.ReadAllText(FixturePath("session-titles.jsonl")).TrimEnd('\n', '\r'));
        try
        {
            SessionStore.AppendCustomTitle(file, "11111111-2222-3333-4444-555555555555", "Renamed \"quoted\" title");
            var info = new FileInfo(file);

            var summary = SessionStore.ReadSummary(file, info.Length, info.LastWriteTimeUtc);

            Assert.Equal("Renamed \"quoted\" title", summary?.Title);
            Assert.Equal("Renamed \"quoted\" title", summary?.CustomTitle);
            Assert.All(File.ReadAllLines(file).Where(l => l.Length > 0), l => Assert.NotNull(TranscriptRecord.Parse(l)));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// The full name, past the sidebar's length, is what Nomad compares to tell its own rename from another's.
    /// </summary>
    [Fact]
    public void TheNewestCustomTitleIsKeptUnshortenedAndAGeneratedTitleIsNotOne()
    {
        var file = Path.Combine(AppContext.BaseDirectory, $"rename-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(file, File.ReadAllText(FixturePath("session-titles.jsonl")).Replace("\"custom-title\"", "\"x-custom-title\""));
        try
        {
            var info = new FileInfo(file);
            Assert.Null(SessionStore.ReadSummary(file, info.Length, info.LastWriteTimeUtc)?.CustomTitle);

            var name = "A name long enough that the sidebar shortens it, renamed from the Claude app on a phone";
            SessionStore.AppendCustomTitle(file, "11111111-2222-3333-4444-555555555555", "An older name");
            SessionStore.AppendCustomTitle(file, "11111111-2222-3333-4444-555555555555", name);
            info.Refresh();

            var summary = SessionStore.ReadSummary(file, info.Length, info.LastWriteTimeUtc);

            Assert.Equal(name, summary?.CustomTitle);
            Assert.Equal(SessionTitles.Shorten(name), summary?.Title);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
