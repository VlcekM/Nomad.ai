using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Agents;

namespace Nomad.Core.Tests;

[Collection(nameof(ChatToolsTests))]
public sealed class ChatToolsTests : IDisposable
{
    private sealed class FakeDirectory : IChatDirectory
    {
        public List<(string? From, string To, string Message)> Sent { get; } = [];

        public Task<IReadOnlyList<ChatInfo>> ListAsync(string? callerKey) =>
            Task.FromResult<IReadOnlyList<ChatInfo>>([new ChatInfo("abc", "Build fixes", "Claude Code", @"C:\Work", "idle")]);

        public Task<string> SendAsync(string? callerKey, string target, string message)
        {
            if (target != "abc")
            {
                throw new ChatMessageException("No such chat.");
            }

            Sent.Add((callerKey, target, message));
            return Task.FromResult("Delivered.");
        }
    }

    private readonly FakeDirectory _directory = new();

    public ChatToolsTests() => ChatTools.Directory = _directory;

    public void Dispose() => ChatTools.Directory = null;

    [Fact]
    public async Task ListsTheOtherChats()
    {
        var text = Text(await new NomadSessionServer(".", "me").HandleAsync(Call("list_chats", new JsonObject())));

        Assert.Equal("- id abc: \"Build fixes\" (Claude Code, idle) in C:\\Work", text);
    }

    [Fact]
    public async Task SendsAsTheCallingChat()
    {
        var response = await new NomadSessionServer(".", "me").HandleAsync(Call("send_to_chat", new JsonObject { ["chat"] = "abc", ["message"] = "Hi" }));

        Assert.False(IsError(response));
        Assert.Equal([("me", "abc", "Hi")], _directory.Sent);
    }

    [Fact]
    public async Task AnUnknownChatIsAnError()
    {
        var response = await ChatTools.HandleAsync(Parse(Call("send_to_chat", new JsonObject { ["chat"] = "zzz", ["message"] = "Hi" })), "me");

        Assert.True(IsError(response));
        Assert.Equal("No such chat.", Text(response));
    }

    [Fact]
    public async Task ASubagentCannotSend()
    {
        var response = await new NomadSessionServer(".", "me").HandleAsync(
            Call("send_to_chat", new JsonObject { ["chat"] = "abc", ["message"] = "Hi" }), mainAgent: false);

        Assert.True(IsError(response));
        Assert.Empty(_directory.Sent);
    }

    [Fact]
    public void AReplyNamesTheSender()
    {
        var envelope = ChatTools.Envelope(new ChatInfo("abc", "Build fixes", "Antigravity", null, "working"), "Done.");

        Assert.Contains("\"Build fixes\" (Antigravity, id abc)", envelope);
        Assert.EndsWith("\n\nDone.", envelope);
    }

    private static JsonElement Call(string name, JsonObject arguments) => Parse(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 7,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
    }.ToJsonString());

    private static JsonElement Parse(JsonElement element) => element;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string Text(JsonObject? response) => response!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static bool IsError(JsonObject? response) => response!["result"]!["isError"]!.GetValue<bool>();
}
