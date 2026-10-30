namespace Nomad.Hub.Contracts;

// The wire types between Nomad and Nomad Hub (docs/hub.md). Shared by the hub (JIT) and the app (NativeAOT), so
// every type here is listed in HubJson.

/// <summary>Enrolls this PC with a one-time token from the hub's log or from another PC's Add device.</summary>
public sealed record EnrollRequest(string Token, string DeviceName);

/// <summary>The device's id and its secret, shown once: Nomad keeps it and sends it as a bearer token.</summary>
public sealed record EnrollResponse(Guid DeviceId, string Secret, string HubVersion);

public sealed record EnrollTokenResponse(string Token, DateTime ExpiresUtc);

public sealed record DeviceDto(Guid Id, string Name, DateTime EnrolledUtc, DateTime LastSeenUtc, bool Online, bool IsThis);

/// <summary>What the hub says about itself and the calling device.</summary>
public sealed record HubInfo(string Version, Guid DeviceId, string DeviceName, bool RelayEnabled);

/// <summary>
/// A project: a repository (or folder) the same on every PC, keyed by something path-independent: the normalised git
/// remote, else the root commit, else the folder name and the device it came from.
/// </summary>
public sealed record ProjectDto(Guid Id, string Key, string Name, string? LocalPath);

public sealed record ProjectFolderRequest(string Path);

/// <summary>One file of a session's payload: a transcript, a subagent's, a Codex index line set, an OpenCode export.</summary>
public sealed record SessionFileDto(string Path, string Hash, long Length);

/// <summary>A session as the hub knows it: its newest version's metadata, files and lease.</summary>
public sealed record SessionDto(
    string Backend,
    string SessionId,
    Guid? ProjectId,
    string? ProjectKey,
    string Title,
    string? WorkingDirectory,
    string? GitBranch,
    DateTime LastActivityUtc,
    long Size,
    int Version,
    IReadOnlyList<SessionFileDto> Files,
    Guid UpdatedBy,
    long Seq,
    bool Deleted,
    Guid? LeaseDevice,
    DateTime? LeaseExpiresUtc);

/// <summary>
/// A new version of a session. <see cref="BaseVersion"/> is the version the files were built on (0 for a new
/// session); the hub refuses the commit when it has moved on since. Every file's blob must be uploaded first.
/// </summary>
public sealed record SessionCommit(
    int BaseVersion,
    string Title,
    string? WorkingDirectory,
    string? GitBranch,
    DateTime LastActivityUtc,
    string? ProjectKey,
    string? ProjectName,
    IReadOnlyList<SessionFileDto> Files);

/// <summary>What a refused commit gets back: the hub's current version, or the blobs it still lacks.</summary>
public sealed record CommitRejection(string Reason, SessionDto? Current, IReadOnlyList<string> MissingBlobs);

/// <summary>A per-session flag Nomad keeps (archived, pinned, …), last writer wins by <see cref="SetUtc"/>.</summary>
public sealed record SessionStateDto(string Backend, string SessionId, string Key, string? Value, DateTime SetUtc, long Seq);

public sealed record SessionStateRequest(string? Value, DateTime SetUtc);

public sealed record LeaseRequest(bool Renew = false);

/// <summary>A lease answer: granted to the caller, or held by <see cref="HolderDevice"/> until <see cref="ExpiresUtc"/>.</summary>
public sealed record LeaseDto(string Backend, string SessionId, bool Granted, Guid? HolderDevice, string? HolderName, DateTime? ExpiresUtc);

public sealed record LeaseKey(string Backend, string SessionId);

/// <summary>Another PC asks the lease holder to hand a session over.</summary>
public sealed record HandoffRequest(string Backend, string SessionId, Guid RequestedBy, string RequestedByName);

/// <summary>Everything that changed after a sequence number.</summary>
public sealed record ChangesDto(long Seq, IReadOnlyList<SessionDto> Sessions, IReadOnlyList<SessionStateDto> States);

/// <summary>
/// One model call's tokens. (Backend, SessionId, MessageId) is the key, so sending a record again changes nothing.
/// </summary>
public sealed record UsageRecordDto(
    string Backend,
    string SessionId,
    string MessageId,
    string? Provider,
    string? Model,
    DateTime AtUtc,
    long Input,
    long Output,
    long Reasoning,
    long CacheRead,
    long CacheWrite,
    double? ReportedUsd);

public sealed record UsageBatch(IReadOnlyList<UsageRecordDto> Records);

public sealed record UsageBatchResult(int Received, int Added);

/// <summary>A day's sums for one backend, model and device (UTC days).</summary>
public sealed record UsageRow(
    DateOnly Day,
    string Backend,
    string? Model,
    Guid Device,
    long Input,
    long Output,
    long Reasoning,
    long CacheRead,
    long CacheWrite,
    double ReportedUsd,
    int Calls);

public sealed record UsageReport(DateTime FromUtc, DateTime ToUtc, IReadOnlyList<UsageRow> Rows, IReadOnlyList<DeviceDto> Devices);

/// <summary>
/// Watching a session through the relay: whether its host streams it, the host's name, and the events of the
/// current turn so far (opaque JSON to the hub; the app reads them). Every event of a stream has a number;
/// <paramref name="Next"/> is the one after the buffer's last, so a viewer can drop the <see cref="HubNames.RelayEvents"/>
/// it already has from the buffer and keep the ones that came while it asked.
/// </summary>
public sealed record WatchResult(bool Live, string? HostName, IReadOnlyList<string> Buffer, long Next);

/// <summary>Names shared by both ends.</summary>
public static class HubNames
{
    public const string HubPath = "/hubs/nomad";

    /// <summary>Server → client: the change sequence moved to this number.</summary>
    public const string Changed = "Changed";

    /// <summary>Server → client: another PC asks for one of this PC's sessions (<see cref="HandoffRequest"/>).</summary>
    public const string HandoffRequested = "HandoffRequested";

    /// <summary>Client → server: renews these leases; answers with the ones no longer held.</summary>
    public const string RenewLeases = "RenewLeases";

    // The relay (docs/hub.md §8): the host publishes its session's events, viewers watch them and send input back.

    /// <summary>Client → server (host): <c>(backend, sessionId, string[] events, bool newTurn)</c>.</summary>
    public const string RelayPublish = "RelayPublish";

    /// <summary>Client → server (host): the session's process ended; viewers get <see cref="RelayEnded"/>.</summary>
    public const string RelayStop = "RelayStop";

    /// <summary>Client → server (viewer): <c>(backend, sessionId)</c> → <see cref="WatchResult"/>.</summary>
    public const string RelayWatch = "RelayWatch";

    public const string RelayUnwatch = "RelayUnwatch";

    /// <summary>Client → server (viewer): <c>(backend, sessionId, string input)</c>, forwarded to the host.</summary>
    public const string RelaySendInput = "RelaySendInput";

    /// <summary>Server → viewers: <c>(backend, sessionId, string[] events, long first)</c>, <c>first</c> the number of the first event.</summary>
    public const string RelayEvents = "RelayEvents";

    /// <summary>Server → host: <c>(backend, sessionId, string input, string fromName)</c>.</summary>
    public const string RelayInput = "RelayInput";

    /// <summary>Server → viewers: <c>(backend, sessionId)</c>, the host stopped streaming it.</summary>
    public const string RelayEnded = "RelayEnded";

    /// <summary>The largest SignalR message either end takes (a tool result or a diff can be large).</summary>
    public const long MaxMessageBytes = 16 * 1024 * 1024;

    public static readonly TimeSpan LeaseRenewal = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Per-session state keys.</summary>
    public const string StateArchived = "archived";
    public const string StatePinned = "pinned";
    public const string StateUnarchived = "unarchived";
    public const string StateInterrupted = "interrupted";
}
