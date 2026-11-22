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

    [Fact]
    public void RecordsThatNamedARemovedOneAsParentArePointedPastIt()
    {
        // As the CLI chains them: a prompt, the saved prompt and its render point after it, then the reply, then a compact
        // boundary whose logical parent names the render point.
        const string prompt = "{\"type\":\"user\",\"uuid\":\"u0\",\"parentUuid\":null,\"message\":{\"role\":\"user\",\"content\":\"Hi\"}}";
        const string snapshot = "{\"parentUuid\":\"u0\",\"type\":\"attachment\",\"attachment\":{\"type\":\"prompt_snapshot\",\"systemPrompt\":[\"Old.\",\"Appended.\"]},\"uuid\":\"s1\"}";
        const string render = "{\"parentUuid\":\"s1\",\"type\":\"attachment\",\"attachment\":{\"type\":\"prompt_render_point\"},\"uuid\":\"r1\"}";
        const string reply = "{\"parentUuid\":\"r1\",\"type\":\"assistant\",\"uuid\":\"a1\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"parentUuid r1 stays here\"}]}}";
        const string boundary = "{\"parentUuid\":null,\"logicalParentUuid\":\"r1\",\"type\":\"system\",\"subtype\":\"compact_boundary\",\"uuid\":\"b1\"}";
        File.WriteAllLines(_file, [prompt, snapshot, render, reply, boundary]);

        Assert.True(PromptSnapshots.DropIfStale(_file, "New."));

        Assert.Equal(
        [
            prompt,
            reply.Replace("\"parentUuid\":\"r1\"", "\"parentUuid\":\"u0\""),
            boundary.Replace("\"logicalParentUuid\":\"r1\"", "\"logicalParentUuid\":\"u0\""),
        ], File.ReadAllLines(_file));
    }

    [Fact]
    public void ARemovedFirstRecordLeavesItsChildWithoutAParent()
    {
        var first = Snapshot("Old.").Replace("\"parentUuid\":\"a\"", "\"parentUuid\":null");
        const string child = "{\"parentUuid\":\"s\",\"type\":\"user\",\"uuid\":\"u1\",\"message\":{\"role\":\"user\",\"content\":\"Hi\"}}";
        File.WriteAllLines(_file, [first, child]);

        Assert.True(PromptSnapshots.DropIfStale(_file, "New."));

        Assert.Equal([child.Replace("\"parentUuid\":\"s\"", "\"parentUuid\":null")], File.ReadAllLines(_file));
    }

    [Fact]
    public void AnAppendedPartWithMoreJoinedAfterItStillCounts()
    {
        File.WriteAllLines(_file, [Snapshot("Prompt.").Replace("\"Appended.\"", "\"Appended.\\n\\nAnother source's text.\""), User]);

        Assert.False(PromptSnapshots.DropIfStale(_file, null, "Appended."));
        Assert.True(PromptSnapshots.DropIfStale(_file, null, "Appended differently."));
    }
}
