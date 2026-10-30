using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Nomad.Hub.Contracts;

namespace Nomad.Core.Hub;

/// <summary>The hub refused a commit (<see cref="CommitRejection.Reason"/>: version, leased, deleted, missing-blobs).</summary>
public sealed class CommitRefusedException(CommitRejection rejection) : Exception($"The hub refused the commit: {rejection.Reason}.")
{
    public CommitRejection Rejection { get; } = rejection;
}

/// <summary>
/// Nomad Hub's HTTP API and its one live connection (SignalR, which reconnects by itself after a drop and here also
/// after the hub closes it). Thread-safe.
/// </summary>
public sealed class HubClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _secret;
    private HubConnection? _connection;
    private CancellationTokenSource? _reconnect;

    public HubClient(Uri baseUrl, string secret, HttpMessageHandler? handler = null)
    {
        BaseUrl = baseUrl;
        _secret = secret;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            BaseAddress = baseUrl,
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Nomad", typeof(HubClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"));
    }

    public Uri BaseUrl { get; }

    /// <summary>The change feed moved on (a sequence number).</summary>
    public event Action<long>? Changed;

    /// <summary>Another PC asks for a session this PC holds.</summary>
    public event Action<HandoffRequest>? HandoffRequested;

    /// <summary>The live connection came up (true) or went down (false).</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>Enrolls with a one-time token; the answer's secret is what <see cref="HubClient"/> is made with.</summary>
    public static async Task<EnrollResponse> EnrollAsync(Uri baseUrl, string token, string deviceName, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.PostAsJsonAsync("api/enroll", new EnrollRequest(token, deviceName), HubJson.Default.EnrollRequest, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("The hub didn't accept that token (it is used once and lasts 24 hours).");
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(HubJson.Default.EnrollResponse, ct).ConfigureAwait(false))!;
    }

    public static string Protect(string secret) => !OperatingSystem.IsWindows() ? Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(protectedSecret));
            }

            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ live connection

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connection is not null)
        {
            return;
        }

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(BaseUrl, HubNames.HubPath.TrimStart('/')), o => o.AccessTokenProvider = () => Task.FromResult<string?>(_secret))
            .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, HubJson.Default))
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();
        connection.On<long>(HubNames.Changed, seq => Changed?.Invoke(seq));
        connection.On<HandoffRequest>(HubNames.HandoffRequested, r => HandoffRequested?.Invoke(r));
        connection.On<string, string, string[], long>(HubNames.RelayEvents, (b, s, e, first) => RelayEvents?.Invoke(b, s, e, first));
        connection.On<string, string, string, string>(HubNames.RelayInput, (b, s, i, from) => RelayInput?.Invoke(b, s, i, from));
        connection.On<string, string>(HubNames.RelayEnded, (b, s) => RelayEnded?.Invoke(b, s));
        connection.Reconnecting += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };
        connection.Reconnected += _ => { ConnectionChanged?.Invoke(true); return Task.CompletedTask; };
        connection.Closed += _ =>
        {
            ConnectionChanged?.Invoke(false);
            StartReconnecting(); // A close the hub chose (a restart, say) isn't retried by SignalR itself.
            return Task.CompletedTask;
        };
        _connection = connection;
        _reconnect = new CancellationTokenSource();
        try
        {
            await connection.StartAsync(ct).ConfigureAwait(false);
            ConnectionChanged?.Invoke(true);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            StartReconnecting();
        }
    }

    private void StartReconnecting()
    {
        if (_connection is not { } connection || _reconnect is not { IsCancellationRequested: false } stop)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(2);
            while (!stop.IsCancellationRequested && connection.State == HubConnectionState.Disconnected)
            {
                try
                {
                    await Task.Delay(delay, stop.Token).ConfigureAwait(false);
                    await connection.StartAsync(stop.Token).ConfigureAwait(false);
                    ConnectionChanged?.Invoke(true);
                    return;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
                }
            }
        });
    }

    /// <summary>Renews these leases; returns the ones another PC holds now. Throws when not connected.</summary>
    public async Task<List<LeaseKey>> RenewLeasesAsync(IReadOnlyList<LeaseKey> keys, CancellationToken ct = default)
    {
        if (_connection is not { State: HubConnectionState.Connected } connection)
        {
            throw new InvalidOperationException("Not connected to the hub.");
        }

        return await connection.InvokeAsync<List<LeaseKey>>(HubNames.RenewLeases, keys.ToList(), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ relay (docs/hub.md §8)

    /// <summary>Events of a session this PC watches: (backend, session id, encoded events, the first one's number).</summary>
    public event Action<string, string, string[], long>? RelayEvents;

    /// <summary>A viewer's input for a session this PC streams: (backend, session id, encoded input, viewer's PC name).</summary>
    public event Action<string, string, string, string>? RelayInput;

    /// <summary>A watched session stopped streaming: (backend, session id).</summary>
    public event Action<string, string>? RelayEnded;

    private HubConnection Live => _connection is { State: HubConnectionState.Connected } connection
        ? connection
        : throw new InvalidOperationException("Not connected to the hub.");

    public Task PublishAsync(string backend, string sessionId, string[] events, bool newTurn, CancellationToken ct = default) =>
        Live.InvokeAsync(HubNames.RelayPublish, backend, sessionId, events, newTurn, ct);

    public Task StopRelayAsync(string backend, string sessionId, CancellationToken ct = default) =>
        Live.InvokeAsync(HubNames.RelayStop, backend, sessionId, ct);

    public Task<WatchResult> WatchAsync(string backend, string sessionId, CancellationToken ct = default) =>
        Live.InvokeAsync<WatchResult>(HubNames.RelayWatch, backend, sessionId, ct);

    public Task UnwatchAsync(string backend, string sessionId, CancellationToken ct = default) =>
        Live.InvokeAsync(HubNames.RelayUnwatch, backend, sessionId, ct);

    public Task SendInputAsync(string backend, string sessionId, string input, CancellationToken ct = default) =>
        Live.InvokeAsync(HubNames.RelaySendInput, backend, sessionId, input, ct);

    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(context.PreviousRetryCount, 6))));
    }

    // ------------------------------------------------------------------ HTTP

    public async Task<HubInfo> InfoAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync("api/info", HubJson.Default.HubInfo, ct).ConfigureAwait(false))!;

    public async Task<List<DeviceDto>> DevicesAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync("api/devices", HubJson.Default.ListDeviceDto, ct).ConfigureAwait(false))!;

    public async Task RevokeDeviceAsync(Guid id, CancellationToken ct = default) =>
        (await _http.DeleteAsync($"api/devices/{id}", ct).ConfigureAwait(false)).EnsureSuccessStatusCode();

    public async Task<EnrollTokenResponse> NewEnrollTokenAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("api/enroll-tokens", null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(HubJson.Default.EnrollTokenResponse, ct).ConfigureAwait(false))!;
    }

    public async Task<List<ProjectDto>> ProjectsAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync("api/projects", HubJson.Default.ListProjectDto, ct).ConfigureAwait(false))!;

    public async Task SetProjectFolderAsync(Guid project, string path, CancellationToken ct = default) =>
        (await _http.PutAsJsonAsync($"api/projects/{project}/folder", new ProjectFolderRequest(path), HubJson.Default.ProjectFolderRequest, ct).ConfigureAwait(false)).EnsureSuccessStatusCode();

    /// <summary>One session as the hub has it; null when it doesn't.</summary>
    public async Task<SessionDto?> SessionAsync(string backend, string sessionId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"api/sessions/{backend}/{Uri.EscapeDataString(sessionId)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(HubJson.Default.SessionDto, ct).ConfigureAwait(false);
    }

    public async Task<ChangesDto> ChangesAsync(long since, CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync($"api/changes?since={since}", HubJson.Default.ChangesDto, ct).ConfigureAwait(false))!;

    public async Task<bool> HasBlobAsync(string hash, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, $"api/blobs/{hash}");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Uploads a file's bytes, or (with a base) only what follows the base blob's first <paramref name="offset"/> bytes.</summary>
    public async Task PutBlobAsync(string hash, Stream content, string? baseHash = null, long offset = 0, CancellationToken ct = default)
    {
        var url = baseHash is null ? $"api/blobs/{hash}" : $"api/blobs/{hash}?base={baseHash}&offset={offset}";
        using var body = new StreamContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PutAsync(url, body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<Stream> GetBlobAsync(string hash, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/blobs/{hash}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    public async Task<SessionDto> CommitAsync(string backend, string sessionId, SessionCommit commit, CancellationToken ct = default)
    {
        using var response = await _http.PutAsJsonAsync($"api/sessions/{backend}/{Uri.EscapeDataString(sessionId)}", commit, HubJson.Default.SessionCommit, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            throw new CommitRefusedException((await response.Content.ReadFromJsonAsync(HubJson.Default.CommitRejection, ct).ConfigureAwait(false))!);
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(HubJson.Default.SessionDto, ct).ConfigureAwait(false))!;
    }

    public async Task DeleteSessionAsync(string backend, string sessionId, CancellationToken ct = default) =>
        (await _http.DeleteAsync($"api/sessions/{backend}/{Uri.EscapeDataString(sessionId)}", ct).ConfigureAwait(false)).EnsureSuccessStatusCode();

    public async Task SetStateAsync(string backend, string sessionId, string key, string? value, DateTime setUtc, CancellationToken ct = default) =>
        (await _http.PutAsJsonAsync($"api/sessions/{backend}/{Uri.EscapeDataString(sessionId)}/state/{key}", new SessionStateRequest(value, setUtc), HubJson.Default.SessionStateRequest, ct).ConfigureAwait(false)).EnsureSuccessStatusCode();

    public async Task<LeaseDto> TakeLeaseAsync(string backend, string sessionId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"api/sessions/{backend}/{Uri.EscapeDataString(sessionId)}/lease", null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(HubJson.Default.LeaseDto, ct).ConfigureAwait(false))!;
    }

    public async Task ReleaseLeaseAsync(string backend, string sessionId, CancellationToken ct = default) =>
        (await _http.DeleteAsync($"api/sessions/{backend}/{Uri.EscapeDataString(sessionId)}/lease", ct).ConfigureAwait(false)).EnsureSuccessStatusCode();

    /// <summary>Asks the holder to hand a session over; false when the holder is offline (wait for its lease to lapse).</summary>
    public async Task<bool> RequestHandoffAsync(string backend, string sessionId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"api/sessions/{backend}/{Uri.EscapeDataString(sessionId)}/handoff", null, ct).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.Conflict && response.IsSuccessStatusCode;
    }

    public async Task<UsageBatchResult> PostUsageAsync(IReadOnlyList<UsageRecordDto> records, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/usage", new UsageBatch(records), HubJson.Default.UsageBatch, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(HubJson.Default.UsageBatchResult, ct).ConfigureAwait(false))!;
    }

    public async Task<UsageReport> UsageAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync($"api/usage?from={fromUtc:O}&to={toUtc:O}", HubJson.Default.UsageReport, ct).ConfigureAwait(false))!;

    public async ValueTask DisposeAsync()
    {
        _reconnect?.Cancel();
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _http.Dispose();
    }
}
