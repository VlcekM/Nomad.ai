using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Nomad.Hub.Contracts;

namespace Nomad.Hub.Tests;

/// <summary>The relay: a host streams its session, a viewer watches it and sends input back.</summary>
public sealed class RelayTests
{
    [Fact]
    public async Task ViewersGetTheTurnSoFarTheStreamAndAnEnd()
    {
        using var hub = new HubFactory();
        await using var host = hub.Connect(await hub.EnrollAsync("PC-A"));
        await using var viewer = hub.Connect(await hub.EnrollAsync("PC-B"));
        var inputs = new TaskCompletionSource<(string Input, string From)>();
        host.On<string, string, string, string>(HubNames.RelayInput, (_, _, input, from) => inputs.TrySetResult((input, from)));
        var streamed = new List<string>();
        var more = new TaskCompletionSource();
        var ended = new TaskCompletionSource();
        viewer.On<string, string, string[]>(HubNames.RelayEvents, (_, _, events) =>
        {
            streamed.AddRange(events);
            more.TrySetResult();
        });
        viewer.On<string, string>(HubNames.RelayEnded, (_, _) => ended.TrySetResult());
        await host.StartAsync();
        await viewer.StartAsync();

        var notLive = await viewer.InvokeAsync<WatchResult>(HubNames.RelayWatch, "claude-code", "s0");
        Assert.False(notLive.Live);

        await host.InvokeAsync(HubNames.RelayPublish, "claude-code", "s1", new[] { "old turn" }, false);
        await host.InvokeAsync(HubNames.RelayPublish, "claude-code", "s1", new[] { "e1", "e2" }, true);
        var watch = await viewer.InvokeAsync<WatchResult>(HubNames.RelayWatch, "claude-code", "s1");
        Assert.Equal((true, "PC-A"), (watch.Live, watch.HostName));
        Assert.Equal(["e1", "e2"], watch.Buffer);

        await host.InvokeAsync(HubNames.RelayPublish, "claude-code", "s1", new[] { "e3" }, false);
        await more.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["e3"], streamed);

        // The turn ended: the hub forgets it, and the stream stays live.
        await host.InvokeAsync(HubNames.RelayPublish, "claude-code", "s1", Array.Empty<string>(), true);
        var idle = await viewer.InvokeAsync<WatchResult>(HubNames.RelayWatch, "claude-code", "s1");
        Assert.True(idle.Live);
        Assert.Empty(idle.Buffer);

        await viewer.InvokeAsync(HubNames.RelaySendInput, "claude-code", "s1", "{\"kind\":\"prompt\"}");
        Assert.Equal(("{\"kind\":\"prompt\"}", "PC-B"), await inputs.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await host.StopAsync(); // The host's connection goes: its streams end.
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await viewer.InvokeAsync<WatchResult>(HubNames.RelayWatch, "claude-code", "s1")).Live);
        await Assert.ThrowsAsync<HubException>(() => viewer.InvokeAsync(HubNames.RelaySendInput, "claude-code", "s1", "{}"));
    }

    [Fact]
    public async Task AHubWithTheRelayOffRefusesIt()
    {
        using var hub = new HubFactory(relay: false);
        await using var host = hub.Connect(await hub.EnrollAsync("PC-A"));
        await host.StartAsync();
        var refused = await Assert.ThrowsAsync<HubException>(() => host.InvokeAsync(HubNames.RelayPublish, "claude-code", "s1", new[] { "e" }, false));
        Assert.Contains("relay is off", refused.Message);
    }
}
