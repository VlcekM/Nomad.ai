using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Nomad.Hub.Contracts;

namespace Nomad.Hub.Tests;

/// <summary>The phone's board (docs/mobile.md): PCs list their open chats, the phone reads and drives them through the hub.</summary>
public sealed class BoardTests
{
    private static BoardChat Chat(string id, string state = "idle") =>
        new("antigravity", id, "Title " + id, "Antigravity", "R:\\Work", state, null, "last line", DateTime.UtcNow, 0);

    [Fact]
    public async Task PcsListTheirChatsAndLeaveWithTheirConnection()
    {
        using var hub = new HubFactory();
        await using var pc = hub.Connect(await hub.EnrollAsync("PC-A"));
        await using var phone = hub.Connect(await hub.EnrollAsync("Phone"));
        var changes = new List<BoardDevice>();
        var changed = new SemaphoreSlim(0);
        phone.On<BoardDevice>(HubNames.BoardChanged, d =>
        {
            lock (changes)
            {
                changes.Add(d);
            }

            changed.Release();
        });
        await pc.StartAsync();
        await phone.StartAsync();

        Assert.Empty(await phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch));

        await pc.InvokeAsync(HubNames.BoardPublish, new List<BoardChat> { Chat("s1"), Chat("s2", "waiting") });
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10)));
        var device = Assert.Single(changes);
        Assert.Equal(("PC-A", 2), (device.DeviceName, device.Chats.Count));
        Assert.Equal("waiting", device.Chats[1].State);

        // A newcomer gets the board as it is.
        var board = await phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch);
        Assert.Equal(2, Assert.Single(board).Chats.Count);

        await pc.StopAsync();
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(device.DeviceId, changes[^1].DeviceId);
        Assert.Empty(changes[^1].Chats);
        Assert.Empty(await phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch));
        var gone = await Assert.ThrowsAsync<HubException>(() =>
            phone.InvokeAsync<ChatPage>(HubNames.ChatHistory, device.DeviceId, "antigravity", "s1", (int?)null, 20));
        Assert.Contains("isn't open", gone.Message);
    }

    [Fact]
    public async Task ThePhoneReadsAndDrivesAChatThroughThePcThatHasItOpen()
    {
        using var hub = new HubFactory();
        await using var pc = hub.Connect(await hub.EnrollAsync("PC-A"));
        await using var phone = hub.Connect(await hub.EnrollAsync("Phone"));
        var asked = new List<(string Backend, string Session, int? Before, int Limit)>();
        pc.On<string, string, int?, int, ChatPage>(HubNames.ChatHistoryRequested, (b, s, before, limit) =>
        {
            asked.Add((b, s, before, limit));
            return Task.FromResult(new ChatPage("idle", 0, 3, [new ChatEntry(2, "assistant", "Pastrami.")]));
        });
        pc.On<string, string, ChatInput, string, ChatInputResult>(HubNames.ChatInputRequested, (_, s, input, from) =>
            Task.FromResult(input.Kind == "prompt"
                ? new ChatInputResult(true, $"{s} got \"{input.Text}\" from {from}")
                : throw new InvalidOperationException("Not offered.")));
        await pc.StartAsync();
        await phone.StartAsync();
        await pc.InvokeAsync(HubNames.BoardPublish, new List<BoardChat> { Chat("s1") });
        var device = Assert.Single(await phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch)).DeviceId;

        var page = await phone.InvokeAsync<ChatPage>(HubNames.ChatHistory, device, "antigravity", "s1", (int?)5, 5000);
        Assert.Equal("Pastrami.", Assert.Single(page.Entries).Text);
        Assert.Equal(("antigravity", "s1", (int?)5, 200), Assert.Single(asked)); // The hub caps a page.

        var sent = await phone.InvokeAsync<ChatInputResult>(HubNames.ChatSend, device, "antigravity", "s1", new ChatInput("prompt", "Hello"));
        Assert.Equal((true, "s1 got \"Hello\" from Phone"), (sent.Ok, sent.Message));

        // A PC that fails to apply it says so; a chat it doesn't list, or another PC's id, never reaches it.
        await Assert.ThrowsAsync<HubException>(() =>
            phone.InvokeAsync<ChatInputResult>(HubNames.ChatSend, device, "antigravity", "s1", new ChatInput("stop")));
        await Assert.ThrowsAsync<HubException>(() =>
            phone.InvokeAsync<ChatInputResult>(HubNames.ChatSend, device, "antigravity", "other", new ChatInput("prompt", "x")));
        await Assert.ThrowsAsync<HubException>(() =>
            phone.InvokeAsync<ChatInputResult>(HubNames.ChatSend, Guid.NewGuid(), "antigravity", "s1", new ChatInput("prompt", "x")));
    }

    [Fact]
    public async Task AHubWithTheRelayOffHasNoBoard()
    {
        using var hub = new HubFactory(relay: false);
        await using var phone = hub.Connect(await hub.EnrollAsync("Phone"));
        await phone.StartAsync();
        var refused = await Assert.ThrowsAsync<HubException>(() => phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch));
        Assert.Contains("relay is off", refused.Message);
    }
}
