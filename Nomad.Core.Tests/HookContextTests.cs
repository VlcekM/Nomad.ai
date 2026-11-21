using System.Text.Json;
using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

public sealed class HookContextTests
{
    [Fact]
    public void ReadsTheJsonAHookPrints() =>
        Assert.Equal("Message sent: 2026-11-21 12:23",
            HookContext.FromOutput("{\"hookSpecificOutput\": {\"hookEventName\": \"UserPromptSubmit\", \"additionalContext\": \"Message sent: 2026-11-21 12:23\"}}\r\n"));

    [Fact]
    public void PlainTextIsAddedAsItIs() => Assert.Equal("Remember the tests.", HookContext.FromOutput("Remember the tests.\n"));

    [Theory]
    [InlineData("")]
    [InlineData("{\"decision\": \"block\", \"reason\": \"Saved.\"}")]
    public void NothingAddedIsNull(string stdout) => Assert.Null(HookContext.FromOutput(stdout));

    [Fact]
    public void ReadsTheSavedAttachment()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"hook_additional_context\",\"content\":[\"Message sent: 11:19\",\"-- Reminders --\"],\"hookName\":\"UserPromptSubmit\"}");

        Assert.Equal("Message sent: 11:19\n-- Reminders --", HookContext.FromAttachment(doc.RootElement));
    }
}
