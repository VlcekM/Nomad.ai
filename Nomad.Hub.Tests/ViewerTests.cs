using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Nomad.Hub.Contracts;

namespace Nomad.Hub.Tests;

/// <summary>
/// A phone is a viewer (docs/mobile.md §6): it reads the board and chats and sends them input through the PC that has them open,
/// and can do nothing else: no sessions, files, leases, devices or tokens, and it revokes only itself. It may fetch the phone app the hub offers.
/// </summary>
public sealed class ViewerTests
{
    [Fact]
    public async Task AddPhonesTokenEnrollsAPhoneThatIsListedAsOne()
    {
        using var hub = new HubFactory();
        var pc = await hub.EnrollAsync("PC-A");

        var token = (await (await pc.PostAsync("/api/enroll-tokens?phone=true", null)).Content.ReadFromJsonAsync(HubJson.Default.EnrollTokenResponse))!;
        var anonymous = hub.CreateClient();
        var enrolled = await anonymous.PostAsJsonAsync("/api/enroll", new EnrollRequest(token.Token, "Pixel"), HubJson.Default.EnrollRequest);
        enrolled.EnsureSuccessStatusCode();

        var devices = (await pc.GetFromJsonAsync("/api/devices", HubJson.Default.ListDeviceDto))!;
        Assert.Equal([("PC-A", false), ("Pixel", true)], devices.Select(d => (d.Name, d.IsPhone)));
    }

    [Fact]
    public async Task APhoneMayOnlyAskWhoItIsAndRevokeItselfInTheApi()
    {
        using var hub = new HubFactory();
        var pc = await hub.EnrollAsync("PC-A");
        var phone = await hub.EnrollAsync("Phone", phone: true);
        var pcId = (await pc.GetFromJsonAsync("/api/info", HubJson.Default.HubInfo))!.DeviceId;
        var phoneId = (await phone.GetFromJsonAsync("/api/info", HubJson.Default.HubInfo))!.DeviceId;

        Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync("/api/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsync("/api/enroll-tokens", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync("/api/changes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync("/api/leases")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.DeleteAsync($"/api/devices/{pcId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pc.GetAsync("/api/devices")).StatusCode); // The PC was left alone.

        // Signing out: it revokes itself, and its secret stops working.
        Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync($"/api/devices/{phoneId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/info")).StatusCode);
    }

    [Fact]
    public async Task APhoneFetchesThePhoneAppTheHubOffers()
    {
        using var hub = new HubFactory();
        var phone = await hub.EnrollAsync("Phone", phone: true);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync("/api/phone-app")).StatusCode); // None offered yet.

        var folder = Directory.CreateDirectory(Path.Combine(hub.DataPath, "phone-app")).FullName;
        byte[] apk = [.. Enumerable.Range(0, 70_000).Select(i => (byte)i)];
        await File.WriteAllBytesAsync(Path.Combine(folder, "Nomad-android.apk"), apk);
        await File.WriteAllTextAsync(Path.Combine(folder, "Nomad-android.json"), """{ "version": "2.9.0", "versionCode": 20900 }""");

        var info = (await phone.GetFromJsonAsync("/api/phone-app", HubJson.Default.PhoneAppInfo))!;
        Assert.Equal(("2.9.0", 20900L, apk.Length), (info.Version, info.VersionCode, info.Size));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(apk)), info.Sha256);
        Assert.Equal(apk, await phone.GetByteArrayAsync("/api/phone-app/apk"));

        // Without the version file it is still offered, unversioned: the phone compares the files.
        File.Delete(Path.Combine(folder, "Nomad-android.json"));
        var bare = (await phone.GetFromJsonAsync("/api/phone-app", HubJson.Default.PhoneAppInfo))!;
        Assert.Equal(("", 0L), (bare.Version, bare.VersionCode));
    }

    [Fact]
    public async Task APhonePutsAFileOnTheHubThatOnlyAPcCanFetch()
    {
        using var hub = new HubFactory();
        var pc = await hub.EnrollAsync("PC-A");
        var phone = await hub.EnrollAsync("Phone", phone: true);
        byte[] photo = [.. Enumerable.Range(0, 90_000).Select(i => (byte)(i * 7))];

        var put = await phone.PostAsync("/api/files?name=" + Uri.EscapeDataString("../../holiday:1.jpg"), new ByteArrayContent(photo));
        put.EnsureSuccessStatusCode();
        var file = (await put.Content.ReadFromJsonAsync(HubJson.Default.ChatFile))!;
        Assert.Equal(("holiday_1.jpg", (long)photo.Length), (file.Name, file.Size)); // No folders, nothing a PC can't name.

        Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync("/api/files/" + file.Id)).StatusCode);
        Assert.Equal(photo, await pc.GetByteArrayAsync("/api/files/" + file.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await pc.GetAsync("/api/files/" + new string('0', 32))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pc.GetAsync("/api/files/..%2F..%2Fhub.db")).StatusCode);

        // A day on, it is gone.
        var store = new PhoneFileStore(new Services.HubOptions { DataPath = hub.DataPath });
        store.Sweep(DateTime.UtcNow + PhoneFileStore.KeepFor + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NotFound, (await pc.GetAsync("/api/files/" + file.Id)).StatusCode);
    }

    [Fact]
    public async Task APhoneWatchesAndSendsInputButCantHostPublishOrDriveTheRelay()
    {
        using var hub = new HubFactory();
        await using var phone = hub.Connect(await hub.EnrollAsync("Phone", phone: true));
        await phone.StartAsync();

        Assert.Empty(await phone.InvokeAsync<IReadOnlyList<BoardDevice>>(HubNames.BoardWatch));
        Assert.False((await phone.InvokeAsync<WatchResult>(HubNames.RelayWatch, "claude-code", "s1")).Live);

        foreach (var refused in new Func<Task>[]
        {
            () => phone.InvokeAsync(HubNames.BoardPublish, new List<BoardChat>()),
            () => phone.InvokeAsync(HubNames.RelayPublish, "claude-code", "s1", new[] { "{}" }, true),
            () => phone.InvokeAsync(HubNames.RelayStop, "claude-code", "s1"),
            () => phone.InvokeAsync(HubNames.RelaySendInput, "claude-code", "s1", "{}"),
            () => phone.InvokeAsync<List<LeaseKey>>(HubNames.RenewLeases, new List<LeaseKey>()),
        })
        {
            var error = await Assert.ThrowsAsync<HubException>(refused);
            Assert.Contains("A phone can't do that", error.Message);
        }
    }
}
