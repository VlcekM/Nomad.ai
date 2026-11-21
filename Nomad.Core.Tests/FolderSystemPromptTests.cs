using Nomad.Core.Settings;

namespace Nomad.Core.Tests;

public sealed class FolderSystemPromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomad-sp-" + Guid.NewGuid().ToString("N"));

    public FolderSystemPromptTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Prompt(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "You are a test.");
        return path;
    }

    [Fact]
    public void AFolderAndTheFoldersInsideItGetItsPrompt()
    {
        var file = Prompt("a.txt");
        var settings = new NomadSettings { FolderSystemPrompts = { [@"C:\Work\Game"] = file } };

        Assert.Equal(file, settings.SystemPromptFor(@"C:\Work\Game"));
        Assert.Equal(file, settings.SystemPromptFor(@"c:/work/game/src/"));
        Assert.Null(settings.SystemPromptFor(@"C:\Work\GameTools"));
        Assert.Null(settings.SystemPromptFor(@"C:\Work"));
        Assert.Null(settings.SystemPromptFor(null));
    }

    [Fact]
    public void TheNearestFolderWins()
    {
        var outer = Prompt("outer.txt");
        var inner = Prompt("inner.txt");
        var settings = new NomadSettings { FolderSystemPrompts = { [@"C:\Work"] = outer, [@"C:\Work\Game\"] = inner } };

        Assert.Equal(inner, settings.SystemPromptFor(@"C:\Work\Game\src"));
        Assert.Equal(outer, settings.SystemPromptFor(@"C:\Work\Other"));
    }

    [Fact]
    public void AMissingFileIsSkippedSoTheSessionStillStarts()
    {
        var settings = new NomadSettings { FolderSystemPrompts = { [@"C:\Work"] = Path.Combine(_root, "gone.txt") } };

        Assert.Null(settings.SystemPromptFor(@"C:\Work"));
    }

    [Fact]
    public void CodexFollowsTheSharedFilesInOlderSettings()
    {
        var file = Prompt("a.txt");
        var settings = new NomadSettings { FolderSystemPrompts = { [@"C:\Work"] = file } };

        Assert.Equal(file, settings.SystemPromptFor(@"C:\Work", codex: true));
    }

    [Fact]
    public void ClaudeCodeAndCodexCanDiffer()
    {
        var claude = Prompt("claude.txt");
        var codex = Prompt("codex.txt");
        var settings = new NomadSettings
        {
            FolderSystemPrompts = { [@"C:\Work"] = claude, [@"C:\Work\Tools"] = "" },
            FolderCodexSystemPrompts = new() { [@"C:\Work"] = "", [@"C:\Work\Tools"] = codex },
        };

        Assert.Equal(claude, settings.SystemPromptFor(@"C:\Work\Game"));
        Assert.Null(settings.SystemPromptFor(@"C:\Work\Game", codex: true));
        Assert.Null(settings.SystemPromptFor(@"C:\Work\Tools"));   // cleared here: the parent's file does not stand in
        Assert.Equal(codex, settings.SystemPromptFor(@"C:\Work\Tools\x", codex: true));
    }
}
