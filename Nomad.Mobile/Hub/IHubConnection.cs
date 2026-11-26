using Nomad.Hub.Contracts;

namespace Nomad.Mobile.Hub;

/// <summary>
/// What the app needs from Nomad Hub: the board, a chat's history, input for it, and its live turn through the relay.
/// <see cref="HubConnection"/> is the real one; the tests use a fake.
/// </summary>
public interface IHubConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Connected (true) or dropped (false). Raised on any thread.</summary>
    event Action<bool>? ConnectionChanged;

    /// <summary>A PC's entry on the board changed. Raised on any thread.</summary>
    event Action<BoardDevice>? BoardChanged;

    /// <summary>Live events of a watched chat: backend, session id, encoded events, the number of the first. Raised on any thread.</summary>
    event Action<string, string, string[], long>? RelayEvents;

    /// <summary>A watched chat's host stopped streaming it. Raised on any thread.</summary>
    event Action<string, string>? RelayEnded;

    /// <summary>A connection attempt failed (the hub refused this phone, say); it is tried again. Raised on any thread.</summary>
    event Action<Exception>? ConnectFailed;

    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Connects again now if the connection is down or waiting to retry (back from the background).</summary>
    Task ReconnectNowAsync(CancellationToken ct = default);

    Task<IReadOnlyList<BoardDevice>> BoardWatchAsync(CancellationToken ct = default);

    Task<ChatPage> ChatHistoryAsync(Guid device, string backend, string sessionId, int? before, int limit, CancellationToken ct = default);

    Task<ChatInputResult> ChatSendAsync(Guid device, string backend, string sessionId, ChatInput input, CancellationToken ct = default);

    Task<WatchResult> WatchAsync(string backend, string sessionId, CancellationToken ct = default);

    Task UnwatchAsync(string backend, string sessionId, CancellationToken ct = default);

    /// <summary>Revokes a device on the hub (this phone, when signing out).</summary>
    Task RevokeDeviceAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Makes hub connections and enrolls this phone; the tests swap in a fake.</summary>
public interface IHubFactory
{
    Task<EnrollResponse> EnrollAsync(Uri hub, string token, string deviceName, CancellationToken ct = default);

    IHubConnection Open(Uri hub, string secret);
}
