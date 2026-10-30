using System.Text.Json;
using Nomad.Core.Agents;
using Nomad.Core.Settings;
using Nomad.Core.Protocol;
using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

public sealed class FolderMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomad-folder-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Destination => Path.Combine(_root, "destination with spaces");

    public FolderMoveTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
    }

    [Fact]
    public async Task MovesOnlyAfterTheUserApprovesTheExactDestination()
    {
        var tool = new FolderMoveTool(Source);
        PermissionRequest? permission = null;
        string? moved = null;
        tool.PermissionRequested += request => permission = request;
        tool.MoveApproved += move => moved = move.Path;

        var call = tool.HandleAsync(Call("../destination with spaces"));
        Assert.False(call.IsCompleted);
        Assert.Null(moved);
        Assert.NotNull(permission);
        Assert.True(permission.SuppressAllowForSession);
        Assert.Equal(Destination, permission.Input.GetProperty("path").GetString());
        Assert.False(tool.Respond(permission with { RequestId = "unrelated" }, new PermissionDecision(PermissionChoice.Allow)));
        Assert.True(tool.Respond(permission, new PermissionDecision(PermissionChoice.Allow)));

        var response = await call;
        Assert.Equal(Destination, moved);
        Assert.False(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DenyingOrCancellingNeverMoves(bool cancel)
    {
        var tool = new FolderMoveTool(Source);
        PermissionRequest? permission = null;
        tool.PermissionRequested += request => permission = request;
        tool.MoveApproved += _ => Assert.Fail("A refused move must not be applied.");
        var call = tool.HandleAsync(Call(Destination));
        if (cancel)
        {
            tool.Cancel();
        }
        else
        {
            tool.Respond(permission!, new PermissionDecision(PermissionChoice.Deny));
        }

        var response = await call;
        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RejectsMissingFoldersAndTheCurrentFolderWithoutAsking()
    {
        var tool = new FolderMoveTool(Source);
        tool.PermissionRequested += _ => Assert.Fail("Invalid destinations must not ask for approval.");
        foreach (var path in new[] { Source + Path.DirectorySeparatorChar, Path.Combine(_root, "missing") })
        {
            var response = await tool.HandleAsync(Call(path));
            Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task RechecksTheFolderAfterApprovalAndRejectsConcurrentMoves()
    {
        var tool = new FolderMoveTool(Source);
        PermissionRequest? permission = null;
        tool.PermissionRequested += request => permission = request;
        tool.MoveApproved += _ => Assert.Fail("The destination was removed before approval.");
        var first = tool.HandleAsync(Call(Destination));
        var second = await tool.HandleAsync(Call(Destination));
        Assert.True(second!["result"]!["isError"]!.GetValue<bool>());
        Directory.Delete(Destination);
        tool.Respond(permission!, new PermissionDecision(PermissionChoice.Allow));
        var result = await first;
        Assert.True(result!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void ApprovedFoldersSurviveSettingsRoundTrip()
    {
        var settings = new NomadSettings();
        settings.MovedSessionFolders["session"] = Destination;
        var file = Path.Combine(_root, "settings.json");
        SettingsStore.Save(settings, file);
        Assert.Equal(Destination, SettingsStore.Load(file).MovedSessionFolders["session"]);
    }

    [Fact]
    public async Task McpCancellationWithdrawsTheApprovalCard()
    {
        var tool = new FolderMoveTool(Source);
        PermissionRequest? permission = null;
        string? withdrawn = null;
        tool.PermissionRequested += request => permission = request;
        tool.PermissionCancelled += id => withdrawn = id;
        tool.MoveApproved += _ => Assert.Fail("A cancelled MCP call must not move.");
        var call = tool.HandleAsync(Call(Destination));
        using var notification = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""");
        Assert.Null(await tool.HandleAsync(notification.RootElement));
        Assert.Equal(permission!.RequestId, withdrawn);
        Assert.True((await call)!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SubagentsCannotMoveTheParentConversation()
    {
        var tool = new FolderMoveTool(Source);
        tool.PermissionRequested += _ => Assert.Fail("A subagent cannot request moving the parent.");
        var response = await tool.HandleAsync(Call(Destination), mainAgent: false);
        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestoredHistoryDistinguishesAFolderHandoffFromAUserInterrupt(bool completedMove)
    {
        const string folder = "C:\\another repo";
        static TranscriptRecord Record(string type, WireContentBlock block) => new() { Type = type, Message = new() { Content = [block] } };
        using var result = JsonDocument.Parse(JsonSerializer.Serialize($"The user approved moving this conversation to {folder}. End your turn now."));
        var history = SessionHistory.FromRecords([
            Record("assistant", new() { Type = "tool_use", Id = "move", Name = FolderMoveTool.ToolName }),
            Record("user", new() { Type = "tool_result", ToolUseId = "move", Content = result.RootElement }),
            Record("user", new() { Type = "text", Text = "[Request interrupted by user]" }),
            Record("assistant", new() { Type = "text", Text = "No response requested." }),
            Record("user", new() { Type = "text", Text = completedMove ? $"Continue the task in {folder}." : "Stay here instead." }),
            Record("assistant", new() { Type = "text", Text = "Ready" }),
        ], truncated: false, "session");

        Assert.Equal(completedMove, history.Items.OfType<HistoryNotice>().Any(n => n.Text == $"Moved to {folder}"));
        Assert.Equal(!completedMove, history.Items.OfType<HistoryNotice>().Any(n => n.Text == "Interrupted"));
        Assert.Equal(!completedMove, history.Items.OfType<HistoryAssistantText>().Any(t => t.Text == "No response requested."));
        Assert.Contains(history.Items, i => i is HistoryAssistantText { Text: "Ready" });
    }

    private static JsonElement Call(string path)
    {
        var message = new System.Text.Json.Nodes.JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "tools/call",
            ["params"] = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "request_folder_move",
                ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["path"] = path, ["reason"] = "The task belongs in this repository." },
            },
        };
        using var document = JsonDocument.Parse(message.ToJsonString());
        return document.RootElement.Clone();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
