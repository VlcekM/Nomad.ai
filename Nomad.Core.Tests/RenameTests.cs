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
            Assert.All(File.ReadAllLines(file).Where(l => l.Length > 0), l => Assert.NotNull(TranscriptRecord.Parse(l)));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
