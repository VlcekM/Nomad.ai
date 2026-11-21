using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Agents;

namespace Nomad.Core.Tests;

public sealed class NewChatToolTests
{
    [Fact]
    public async Task ListsItsTools()
    {
        var response = await new NomadSessionServer(".").HandleAsync(Message("tools/list", null));

        var names = response!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>());
        Assert.Equal(["request_folder_move", "suggest_new_chat", "list_chats", "send_to_chat"], names);
    }

    [Fact]
    public async Task ASuggestionAnswersAtOnceWithoutAsking()
    {
        var server = new NomadSessionServer(".");
        server.FolderMove.PermissionRequested += _ => Assert.Fail("A suggestion needs no approval.");

        var call = server.HandleAsync(Suggest("Fix the flaky test", "Fix the flaky test in FooTests.cs: it races on the timer."));

        Assert.True(call.IsCompleted);
        Assert.False((await call)!["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("", "Do it.")]
    [InlineData("Title", " ")]
    public async Task ASuggestionNeedsATitleAndAPrompt(string title, string prompt)
    {
        var response = await new NomadSessionServer(".").HandleAsync(Suggest(title, prompt));

        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SubagentsCannotSuggestANewChat()
    {
        var response = await new NomadSessionServer(".").HandleAsync(Suggest("Title", "Do it."), mainAgent: false);

        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void OwnsOnlyItsOwnTools()
    {
        Assert.True(NomadSessionServer.Owns(NewChatTool.ToolName));
        Assert.True(NomadSessionServer.Owns(FolderMoveTool.ToolName));
        Assert.False(NomadSessionServer.Owns("Bash"));
    }

    private static JsonElement Suggest(string title, string prompt) => Message("tools/call", new JsonObject
    {
        ["name"] = "suggest_new_chat",
        ["arguments"] = new JsonObject { ["title"] = title, ["prompt"] = prompt },
    });

    private static JsonElement Message(string method, JsonObject? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = method, ["params"] = parameters };
        using var document = JsonDocument.Parse(message.ToJsonString());
        return document.RootElement.Clone();
    }
}
