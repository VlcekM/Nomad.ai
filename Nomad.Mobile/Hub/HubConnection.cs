using Nomad.Core.Hub;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.Hub;

/// <summary>The real hub connection: Nomad's own <see cref="HubClient"/>, which reconnects a dropped connection itself.</summary>
public sealed class HubConnection : IHubConnection
{
    private readonly HubClient _client;

    public HubConnection(Uri hub, string secret)
    {
        _client = new HubClient(hub, secret);
        _client.ConnectionChanged += up => ConnectionChanged?.Invoke(up);
        _client.BoardChanged += d => BoardChanged?.Invoke(d);
        _client.RelayEvents += (b, s, e, first) => RelayEvents?.Invoke(b, s, e, first);
        _client.RelayEnded += (b, s) => RelayEnded?.Invoke(b, s);
        _client.ConnectFailed += ex => ConnectFailed?.Invoke(ex);
    }

    public bool IsConnected => _client.IsConnected;

    public event Action<bool>? ConnectionChanged;

    public event Action<BoardDevice>? BoardChanged;

    public event Action<string, string, string[], long>? RelayEvents;

    public event Action<string, string>? RelayEnded;

    public event Action<Exception>? ConnectFailed;

    public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);

    public Task ReconnectNowAsync(CancellationToken ct = default) => _client.ReconnectNowAsync(ct);

    public Task<IReadOnlyList<BoardDevice>> BoardWatchAsync(CancellationToken ct = default) => _client.BoardWatchAsync(ct);

    public Task<ChatPage> ChatHistoryAsync(Guid device, string backend, string sessionId, int? before, int limit, CancellationToken ct = default) =>
        _client.ChatHistoryAsync(device, backend, sessionId, before, limit, ct);

    public Task<ChatInputResult> ChatSendAsync(Guid device, string backend, string sessionId, ChatInput input, CancellationToken ct = default) =>
        _client.ChatSendAsync(device, backend, sessionId, input, ct);

    public Task<ChatSettings> ChatSettingsAsync(Guid device, string backend, string sessionId, CancellationToken ct = default) =>
        _client.ChatSettingsAsync(device, backend, sessionId, ct);

    public Task<NewChatOptions> NewChatOptionsAsync(Guid device, CancellationToken ct = default) => _client.NewChatOptionsAsync(device, ct);

    public Task<NewChatResult> ChatStartAsync(Guid device, NewChat chat, CancellationToken ct = default) => _client.ChatStartAsync(device, chat, ct);

    public Task<WatchResult> WatchAsync(string backend, string sessionId, CancellationToken ct = default) => _client.WatchAsync(backend, sessionId, ct);

    public Task UnwatchAsync(string backend, string sessionId, CancellationToken ct = default) => _client.UnwatchAsync(backend, sessionId, ct);

    public Task RevokeDeviceAsync(Guid id, CancellationToken ct = default) => _client.RevokeDeviceAsync(id, ct);

    public Task<PhoneAppInfo?> PhoneAppAsync(CancellationToken ct = default) => _client.PhoneAppAsync(ct);

    public Task<Stream> PhoneAppDownloadAsync(CancellationToken ct = default) => _client.PhoneAppDownloadAsync(ct);

    public Task<ChatFile> UploadFileAsync(Stream content, string name, long? length, CancellationToken ct = default) =>
        _client.UploadFileAsync(content, name, length, ct);

    public Task<ChatFile> ChatFileAsync(Guid device, string backend, string sessionId, ChatFileRequest request, CancellationToken ct = default) =>
        _client.ChatFileAsync(device, backend, sessionId, request, ct);

    public Task<Stream> DownloadFileAsync(string id, CancellationToken ct = default) => _client.DownloadFileAsync(id, ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

public sealed class HubFactory : IHubFactory
{
    public Task<EnrollResponse> EnrollAsync(Uri hub, string token, string deviceName, CancellationToken ct = default) =>
        HubClient.EnrollAsync(hub, token, deviceName, ct);

    public IHubConnection Open(Uri hub, string secret) => new HubConnection(hub, secret);
}
