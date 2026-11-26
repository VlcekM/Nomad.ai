using System.Net.Http.Json;
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
        await using var phone = hub.Connect(await hub.EnrollAsync("Phone", phone: true));
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
        await using var phone = hub.Connect(await hub.EnrollAsync("Phone", phone: true));
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
    public async Task APhoneStartsAChatAndReadsItsSettingsThroughThePc()
    {
        using var hub = new HubFactory();
        await using var pc = hub.Connect(await hub.EnrollAsync("PC-A"));
        await using var phone = hub.Connect(await hub.EnrollAsync("Phone", phone: true));
        var started = new List<(NewChat Chat, string From)>();
        var settings = new ChatSettings("opus", [new ChatChoice("opus", "Opus")], "high", [new ChatChoice("high", "High")], "plan", [new ChatChoice("plan", "Plan")]);
        pc.On<NewChatOptions>(HubNames.NewChatOptionsRequested, () =>
            Task.FromResult(new NewChatOptions([new NewChatAgent("claude-code", "Claude Code", settings)], "claude-code", ["R:\\Work"])));
        pc.On<NewChat, string, NewChatResult>(HubNames.ChatStartRequested, (chat, from) =>
        {
            started.Add((chat, from));
            return Task.FromResult(new NewChatResult(true, "Started.", "s9"));
        });
        pc.On<string, string, ChatSettings>(HubNames.ChatSettingsRequested, (_, _) => Task.FromResult(settings));
        await pc.StartAsync();
        await phone.StartAsync();

        // A PC with no chats open is still on the board, online, and takes a new chat.
        await pc.InvokeAsync(HubNames.BoardPublish, new List<BoardChat>());
        var device = Assert.Single(await phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch));
        Assert.True(device.Online);
        Assert.Empty(device.Chats);

        var options = await phone.InvokeAsync<NewChatOptions>(HubNames.NewChatOptionsGet, device.DeviceId);
        Assert.Equal("Opus", Assert.Single(options.Agents).Settings.Models.Single().Name);
        var result = await phone.InvokeAsync<NewChatResult>(HubNames.ChatStart, device.DeviceId,
            new NewChat("claude-code", "R:\\Work", "opus", "high", "plan", "Hello"));
        Assert.Equal((true, "s9"), (result.Ok, result.SessionId));
        Assert.Equal(("Hello", "Phone"), (started.Single().Chat.Prompt, started.Single().From));

        // A chat's settings come from the PC that has it open.
        await pc.InvokeAsync(HubNames.BoardPublish, new List<BoardChat> { Chat("s9") });
        Assert.Equal("plan", (await phone.InvokeAsync<ChatSettings>(HubNames.ChatSettingsGet, device.DeviceId, "antigravity", "s9")).Mode);

        // No PC by that id: refused by the hub, never reaching a PC.
        await Assert.ThrowsAsync<HubException>(() => phone.InvokeAsync<NewChatOptions>(HubNames.NewChatOptionsGet, Guid.NewGuid()));

        // Gone, it is offline.
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
        await phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch);
        await pc.StopAsync();
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(changes[^1].Online);
    }

    [Fact]
    public async Task AHubWithTheRelayOffHasNoBoard()
    {
        using var hub = new HubFactory(relay: false);
        await using var phone = hub.Connect(await hub.EnrollAsync("Phone", phone: true));
        await phone.StartAsync();
        var refused = await Assert.ThrowsAsync<HubException>(() => phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch));
        Assert.Contains("relay is off", refused.Message);
    }

    [Fact]
    public async Task ARevokedPhoneIsCutOffAtOnce()
    {
        using var hub = new HubFactory();
        var pcHttp = await hub.EnrollAsync("PC-A");
        var phoneHttp = await hub.EnrollAsync("Phone", phone: true);
        await using var phone = hub.Connect(phoneHttp);
        var closed = new TaskCompletionSource();
        phone.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await phone.StartAsync();
        await phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch);
        var phoneId = (await phoneHttp.GetFromJsonAsync<HubInfo>("/api/info"))!.DeviceId;

        (await pcHttp.DeleteAsync($"/api/devices/{phoneId}")).EnsureSuccessStatusCode();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HubConnectionState.Disconnected, phone.State);
        await using var again = hub.Connect(phoneHttp);
        await Assert.ThrowsAnyAsync<Exception>(() => again.StartAsync()); // Its secret no longer signs in.
    }

    [Fact]
    public async Task APcConnectedTwiceShowsOnceAndIsAskedThroughItsLatestConnection()
    {
        using var hub = new HubFactory();
        var pcHttp = await hub.EnrollAsync("PC-A");
        await using var old = hub.Connect(pcHttp);
        await using var fresh = hub.Connect(pcHttp);
        await using var phone = hub.Connect(await hub.EnrollAsync("Phone", phone: true));
        old.On<string, string, int?, int, ChatPage>(HubNames.ChatHistoryRequested, (_, _, _, _) => Task.FromResult(new ChatPage("idle", 0, 1, [new ChatEntry(0, "assistant", "old")])));
        fresh.On<string, string, int?, int, ChatPage>(HubNames.ChatHistoryRequested, (_, _, _, _) => Task.FromResult(new ChatPage("idle", 0, 1, [new ChatEntry(0, "assistant", "fresh")])));
        await old.StartAsync();
        await fresh.StartAsync();
        await phone.StartAsync();

        await old.InvokeAsync(HubNames.BoardPublish, new List<BoardChat> { Chat("s1"), Chat("only-old") });
        await fresh.InvokeAsync(HubNames.BoardPublish, new List<BoardChat> { Chat("s1") });

        var device = Assert.Single(await phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch));
        Assert.Equal(["s1", "only-old"], device.Chats.Select(c => c.SessionId));
        var page = await phone.InvokeAsync<ChatPage>(HubNames.ChatHistory, device.DeviceId, "antigravity", "s1", (int?)null, 20);
        Assert.Equal("fresh", Assert.Single(page.Entries).Text);

        // The old connection goes: the PC stays, with the chats of the one left.
        var dropped = new TaskCompletionSource<BoardDevice>();
        phone.On<BoardDevice>(HubNames.BoardChanged, d =>
        {
            if (d.Chats.Count == 1)
            {
                dropped.TrySetResult(d);
            }
        });
        await old.StopAsync();
        Assert.Equal(["s1"], (await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10))).Chats.Select(c => c.SessionId));
        var after = await phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch);
        Assert.Equal(["s1"], Assert.Single(after).Chats.Select(c => c.SessionId));
        Assert.True(Assert.Single(after).Seq > device.Seq);
    }
}
