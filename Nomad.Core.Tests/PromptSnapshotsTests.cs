using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

public sealed class PromptSnapshotsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "nomad-snap-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose() => File.Delete(_file);

    private static string Snapshot(string prompt) =>
        "{\"parentUuid\":\"a\",\"type\":\"attachment\",\"attachment\":{\"type\":\"prompt_snapshot\",\"systemPrompt\":[" +
        System.Text.Json.JsonSerializer.Serialize(prompt) + ",\"Appended.\"]},\"uuid\":\"s\"}";

    private const string User = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"mentions prompt_snapshot in passing\"},\"uuid\":\"u\"}";
    private const string RenderPoint = "{\"type\":\"attachment\",\"attachment\":{\"type\":\"prompt_render_point\"},\"uuid\":\"r\"}";

    [Fact]
    public void AStaleSavedPromptIsDroppedAndNothingElse()
    {
        File.WriteAllLines(_file, [Snapshot("The word is APPLE.\n"), User, RenderPoint]);

        Assert.True(PromptSnapshots.DropIfStale(_file, "The word is BANANA."));

        Assert.Equal([User], File.ReadAllLines(_file));
    }

    [Fact]
    public void TheCurrentPromptIsLeftInPlace()
    {
        File.WriteAllLines(_file, [Snapshot("The word is APPLE.\n"), User]);

        Assert.False(PromptSnapshots.DropIfStale(_file, "The word is APPLE.\r\n"));

        Assert.Equal(2, File.ReadAllLines(_file).Length);
    }

    [Fact]
    public void OnlyTheLatestSavedPromptCounts()
    {
        File.WriteAllLines(_file, [Snapshot("Old."), User, Snapshot("New.")]);

        Assert.False(PromptSnapshots.DropIfStale(_file, "New."));
    }

    [Fact]
    public void ADifferentAppendedPartIsStaleWithOrWithoutAPromptFile()
    {
        File.WriteAllLines(_file, [Snapshot("The CLI's own prompt."), User]);

        Assert.False(PromptSnapshots.DropIfStale(_file, null, "Appended.")); // The saved copy still has it.
        Assert.True(PromptSnapshots.DropIfStale(_file, null, "Appended, without the new-chat paragraph."));
        Assert.Equal([User], File.ReadAllLines(_file));
    }

    [Fact]
    public void BothPartsMustMatchToKeepTheSavedPrompt()
    {
        File.WriteAllLines(_file, [Snapshot("The word is APPLE."), User]);

        Assert.False(PromptSnapshots.DropIfStale(_file, "The word is APPLE.", "Appended."));
        Assert.True(PromptSnapshots.DropIfStale(_file, "The word is APPLE.", "Something else."));
    }
}
