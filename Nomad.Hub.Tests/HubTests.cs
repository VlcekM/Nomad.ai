using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Nomad.Hub;
using Nomad.Hub.Contracts;
using Nomad.Hub.Data;
using Nomad.Hub.Services;

namespace Nomad.Hub.Tests;

public sealed class HubFactory : WebApplicationFactory<Program>
{
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "nomad-hub-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Hub:DataPath", DataPath);

    public async Task<HttpClient> EnrollAsync(string name)
    {
        string token;
        await using (var scope = Services.CreateAsyncScope())
        {
            token = (await DeviceEndpoints.NewEnrollTokenAsync(scope.ServiceProvider.GetRequiredService<HubDb>(), null)).Token;
        }

        var anonymous = CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/enroll", new EnrollRequest(token, name), HubJson.Default.EnrollRequest);
        response.EnsureSuccessStatusCode();
        var enrolled = (await response.Content.ReadFromJsonAsync(HubJson.Default.EnrollResponse))!;
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Secret);
        return client;
    }

    public HubConnection Connect(HttpClient client) => new HubConnectionBuilder()
        .WithUrl(new Uri(Server.BaseAddress, HubNames.HubPath), o =>
        {
            o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.Headers["Authorization"] = client.DefaultRequestHeaders.Authorization!.ToString();
        })
        .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, HubJson.Default))
        .Build();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(DataPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class HubTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<SessionFileDto> PutBlobAsync(HttpClient client, string path, byte[] content)
    {
        var hash = Sha(content);
        (await client.PutAsync($"/api/blobs/{hash}", new ByteArrayContent(content))).EnsureSuccessStatusCode();
        return new SessionFileDto(path, hash, content.Length);
    }

    private static SessionCommit Commit(int baseVersion, params SessionFileDto[] files) =>
        new(baseVersion, "A session", @"R:\work\repo", "main", DateTime.UtcNow, "github.com/me/repo", "repo", files);

    private static Task<HttpResponseMessage> PutSessionAsync(HttpClient client, string id, SessionCommit commit) =>
        client.PutAsJsonAsync($"/api/sessions/claude-code/{id}", commit, HubJson.Default.SessionCommit);

    [Fact]
    public async Task EnrollsAndRefusesStrangers()
    {
        var anonymous = _hub.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/info")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/enroll", new EnrollRequest("nope", "x"), HubJson.Default.EnrollRequest)).StatusCode);
        Assert.Equal("ok", await anonymous.GetStringAsync("/health"));

        var pc = await _hub.EnrollAsync("PC-A");
        var info = await pc.GetFromJsonAsync("/api/info", HubJson.Default.HubInfo);
        Assert.Equal("PC-A", info!.DeviceName);

        var token = await (await pc.PostAsync("/api/enroll-tokens", null)).Content.ReadFromJsonAsync(HubJson.Default.EnrollTokenResponse);
        var second = await anonymous.PostAsJsonAsync("/api/enroll", new EnrollRequest(token!.Token, "PC-B"), HubJson.Default.EnrollRequest);
        second.EnsureSuccessStatusCode();
        var reused = await anonymous.PostAsJsonAsync("/api/enroll", new EnrollRequest(token.Token, "PC-C"), HubJson.Default.EnrollRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        var devices = (await pc.GetFromJsonAsync("/api/devices", HubJson.Default.ListDeviceDto))!;
        Assert.Equal(["PC-A", "PC-B"], devices.Select(d => d.Name));
        var b = devices.Single(d => d.Name == "PC-B");
        Assert.Equal(HttpStatusCode.NoContent, (await pc.DeleteAsync($"/api/devices/{b.Id}")).StatusCode);
        Assert.Single((await pc.GetFromJsonAsync("/api/devices", HubJson.Default.ListDeviceDto))!);
    }

    [Fact]
    public async Task CommitsAppendsAndFeedsChanges()
    {
        var a = await _hub.EnrollAsync("PC-A");
        var b = await _hub.EnrollAsync("PC-B");
        var first = Encoding.UTF8.GetBytes("{\"a\":1}\n");
        var file = await PutBlobAsync(a, "main.jsonl", first);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await PutSessionAsync(a, "s1", Commit(0, file with { Hash = new string('0', 64) }))).StatusCode);

        var v1 = await (await PutSessionAsync(a, "s1", Commit(0, file))).Content.ReadFromJsonAsync(HubJson.Default.SessionDto);
        Assert.Equal(1, v1!.Version);

        // Append: only the tail goes up, on top of the first version's blob.
        var tail = Encoding.UTF8.GetBytes("{\"b\":2}\n");
        var whole = first.Concat(tail).ToArray();
        var hash = Sha(whole);
        var append = await a.PutAsync($"/api/blobs/{hash}?base={file.Hash}&offset={first.Length}", new ByteArrayContent(tail));
        Assert.Equal(HttpStatusCode.NoContent, append.StatusCode);
        var bad = await a.PutAsync($"/api/blobs/{Sha(tail)}?base={file.Hash}&offset={first.Length}", new ByteArrayContent(tail));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        var grown = new SessionFileDto("main.jsonl", hash, whole.Length);
        var stale = await PutSessionAsync(b, "s1", Commit(0, grown));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version", (await stale.Content.ReadFromJsonAsync(HubJson.Default.CommitRejection))!.Reason);

        var v2 = await (await PutSessionAsync(b, "s1", Commit(1, grown))).Content.ReadFromJsonAsync(HubJson.Default.SessionDto);
        Assert.Equal(2, v2!.Version);
        Assert.Equal(whole, await a.GetByteArrayAsync($"/api/blobs/{hash}"));

        var all = await a.GetFromJsonAsync("/api/changes?since=0", HubJson.Default.ChangesDto);
        var s = Assert.Single(all!.Sessions);
        Assert.Equal(2, s.Version);
        Assert.Equal("github.com/me/repo", s.ProjectKey);
        Assert.Equal(hash, Assert.Single(s.Files).Hash);
        var none = await a.GetFromJsonAsync($"/api/changes?since={all.Seq}", HubJson.Default.ChangesDto);
        Assert.Empty(none!.Sessions);

        // The first committer's folder is remembered for the project, per device.
        var projects = await a.GetFromJsonAsync("/api/projects", HubJson.Default.ListProjectDto);
        Assert.Equal(@"R:\work\repo", Assert.Single(projects!).LocalPath);

        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync("/api/sessions/claude-code/s1")).StatusCode);
        var deleted = await a.GetFromJsonAsync($"/api/changes?since={all.Seq}", HubJson.Default.ChangesDto);
        Assert.True(Assert.Single(deleted!.Sessions).Deleted);
        var afterDelete = await PutSessionAsync(a, "s1", Commit(2, grown));
        Assert.Equal("deleted", (await afterDelete.Content.ReadFromJsonAsync(HubJson.Default.CommitRejection))!.Reason);
    }

    [Fact]
    public async Task LeasesKeepOneHost()
    {
        var a = await _hub.EnrollAsync("PC-A");
        var b = await _hub.EnrollAsync("PC-B");
        var lease = await (await a.PostAsync("/api/sessions/claude-code/s1/lease", null)).Content.ReadFromJsonAsync(HubJson.Default.LeaseDto);
        Assert.True(lease!.Granted);

        var refused = await (await b.PostAsync("/api/sessions/claude-code/s1/lease", null)).Content.ReadFromJsonAsync(HubJson.Default.LeaseDto);
        Assert.False(refused!.Granted);
        Assert.Equal("PC-A", refused.HolderName);

        var file = await PutBlobAsync(b, "main.jsonl", "x\n"u8.ToArray());
        var leased = await PutSessionAsync(b, "s1", Commit(0, file));
        Assert.Equal("leased", (await leased.Content.ReadFromJsonAsync(HubJson.Default.CommitRejection))!.Reason);

        await a.DeleteAsync("/api/sessions/claude-code/s1/lease");
        var taken = await (await b.PostAsync("/api/sessions/claude-code/s1/lease", null)).Content.ReadFromJsonAsync(HubJson.Default.LeaseDto);
        Assert.True(taken!.Granted);
    }

    [Fact]
    public async Task StateIsLastWriterWins()
    {
        var a = await _hub.EnrollAsync("PC-A");
        var now = DateTime.UtcNow;
        await a.PutAsJsonAsync("/api/sessions/claude-code/s1/state/archived", new SessionStateRequest("1", now), HubJson.Default.SessionStateRequest);
        var older = await a.PutAsJsonAsync("/api/sessions/claude-code/s1/state/archived", new SessionStateRequest(null, now.AddMinutes(-1)), HubJson.Default.SessionStateRequest);
        Assert.Equal(HttpStatusCode.AlreadyReported, older.StatusCode);

        var changes = await a.GetFromJsonAsync("/api/changes?since=0", HubJson.Default.ChangesDto);
        var state = Assert.Single(changes!.States);
        Assert.Equal(("archived", "1"), (state.Key, state.Value));
    }

    [Fact]
    public async Task UsageIsIdempotentAndSummed()
    {
        var a = await _hub.EnrollAsync("PC-A");
        var at = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        var batch = new UsageBatch([
            new UsageRecordDto("claude-code", "s1", "m1", "anthropic", "claude-opus-5-5", at, 10, 20, 0, 100, 5, null),
            new UsageRecordDto("claude-code", "s1", "m2", "anthropic", "claude-opus-5-5", at.AddHours(1), 1, 2, 0, 0, 0, null),
            new UsageRecordDto("opencode", "ses_1", "msg_1", "opencode", "big-pickle", at, 3, 4, 1, 0, 0, 0.25),
        ]);
        var first = await (await a.PostAsJsonAsync("/api/usage", batch, HubJson.Default.UsageBatch)).Content.ReadFromJsonAsync(HubJson.Default.UsageBatchResult);
        Assert.Equal(3, first!.Added);
        var again = await (await a.PostAsJsonAsync("/api/usage", batch, HubJson.Default.UsageBatch)).Content.ReadFromJsonAsync(HubJson.Default.UsageBatchResult);
        Assert.Equal(0, again!.Added);

        var report = await a.GetFromJsonAsync("/api/usage?from=2026-10-01T00:00:00Z&to=2026-11-01T00:00:00Z", HubJson.Default.UsageReport);
        var claude = report!.Rows.Single(r => r.Backend == "claude-code");
        Assert.Equal((11, 22, 100, 2), (claude.Input, claude.Output, claude.CacheRead, claude.Calls));
        Assert.Equal(new DateOnly(2026, 10, 30), claude.Day);
        Assert.Equal(0.25, report.Rows.Single(r => r.Backend == "opencode").ReportedUsd);
    }

    [Fact]
    public async Task ConnectionGetsChangesRenewsLeasesAndHandoffs()
    {
        var a = await _hub.EnrollAsync("PC-A");
        var b = await _hub.EnrollAsync("PC-B");
        await using var hubA = _hub.Connect(a);
        var changed = new TaskCompletionSource<long>();
        var handoff = new TaskCompletionSource<HandoffRequest>();
        hubA.On<long>(HubNames.Changed, seq => changed.TrySetResult(seq));
        hubA.On<HandoffRequest>(HubNames.HandoffRequested, r => handoff.TrySetResult(r));
        await hubA.StartAsync();

        var lost = await hubA.InvokeAsync<List<LeaseKey>>(HubNames.RenewLeases, new List<LeaseKey> { new("claude-code", "s1") });
        Assert.Empty(lost);
        Assert.True(await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)) > 0);

        var devices = (await b.GetFromJsonAsync("/api/devices", HubJson.Default.ListDeviceDto))!;
        Assert.True(devices.Single(d => d.Name == "PC-A").Online);

        Assert.Equal(HttpStatusCode.Accepted, (await b.PostAsync("/api/sessions/claude-code/s1/handoff", null)).StatusCode);
        var request = await handoff.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("s1", "PC-B"), (request.SessionId, request.RequestedByName));

        var bLease = await (await b.PostAsync("/api/sessions/claude-code/s1/lease", null)).Content.ReadFromJsonAsync(HubJson.Default.LeaseDto);
        Assert.False(bLease!.Granted);
        await a.DeleteAsync("/api/sessions/claude-code/s1/lease");
        Assert.True((await (await b.PostAsync("/api/sessions/claude-code/s1/lease", null)).Content.ReadFromJsonAsync(HubJson.Default.LeaseDto))!.Granted);
        var nowLost = await hubA.InvokeAsync<List<LeaseKey>>(HubNames.RenewLeases, new List<LeaseKey> { new("claude-code", "s1") });
        Assert.Single(nowLost);
    }

    [Fact]
    public async Task SweepKeepsTheNewestVersionsAndTheirBlobs()
    {
        var a = await _hub.EnrollAsync("PC-A");
        var hashes = new List<string>();
        for (var v = 0; v < 5; v++)
        {
            var file = await PutBlobAsync(a, "main.jsonl", Encoding.UTF8.GetBytes($"version {v}\n"));
            hashes.Add(file.Hash);
            (await PutSessionAsync(a, "s1", Commit(v, file))).EnsureSuccessStatusCode();
        }

        var blobs = _hub.Services.GetRequiredService<BlobStore>();
        foreach (var hash in hashes)
        {
            File.SetLastWriteTimeUtc(blobs.PathOf(hash), DateTime.UtcNow.AddHours(-2));
        }

        await using (var scope = _hub.Services.CreateAsyncScope())
        {
            await _hub.Services.GetRequiredService<Maintenance>().SweepAsync(scope.ServiceProvider.GetRequiredService<HubDb>(), CancellationToken.None);
        }

        Assert.Equal([false, false, true, true, true], hashes.Select(blobs.Exists));
    }
}
