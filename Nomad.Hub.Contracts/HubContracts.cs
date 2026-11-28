namespace Nomad.Hub.Contracts;

// The wire types between Nomad and Nomad Hub (docs/hub.md). Shared by the hub (JIT) and the app (NativeAOT), so
// every type here is listed in HubJson.

/// <summary>Enrolls this PC with a one-time token from the hub's log or from another PC's Add device.</summary>
public sealed record EnrollRequest(string Token, string DeviceName);

/// <summary>The device's id and its secret, shown once: Nomad keeps it and sends it as a bearer token.</summary>
public sealed record EnrollResponse(Guid DeviceId, string Secret, string HubVersion);

public sealed record EnrollTokenResponse(string Token, DateTime ExpiresUtc);

/// <summary>An enrolled device; <paramref name="IsPhone"/>: a phone, which only reads and answers chats (docs/mobile.md §6).</summary>
public sealed record DeviceDto(Guid Id, string Name, DateTime EnrolledUtc, DateTime LastSeenUtc, bool Online, bool IsThis, bool IsPhone = false);

/// <summary>What the hub says about itself and the calling device.</summary>
public sealed record HubInfo(string Version, Guid DeviceId, string DeviceName, bool RelayEnabled);

/// <summary>One category of disk space used on the hub (a backend's sessions, database, backups, etc.).</summary>
public sealed record StorageCategoryDto(string Key, string Label, long Bytes, int Count);

/// <summary>Disk space used by the hub, broken down by category.</summary>
public sealed record HubStorageDto(
    long TotalBytes,
    long? DiskFreeBytes,
    long? DiskTotalBytes,
    int SessionCount,
    int BlobCount,
    IReadOnlyList<StorageCategoryDto> Categories);

/// <summary>
/// The phone app the hub offers (docs/mobile.md §5, "Updating the app"): its version as the release names it ("2.9.0") and as
/// Android's version code (20900; 0 when the hub wasn't told), the file's size and SHA-256 (lowercase hex), and when it was
/// put there. A phone with a lower version code, or the same one and a different file, offers it as an update.
/// </summary>
public sealed record PhoneAppInfo(string Version, long VersionCode, long Size, string Sha256, DateTime PublishedUtc);

/// <summary>
/// A file a phone put on the hub for a prompt (<c>POST api/files?name=</c>): its id, which the PC fetches it by
/// (<c>GET api/files/{id}</c>), its name and size. The hub keeps it a day.
/// </summary>
public sealed record ChatFile(string Id, string Name, long Size);

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

// The phone (docs/mobile.md): each Nomad lists the chats it has open on the board, and answers for them; the hub routes.

/// <summary>
/// One chat in Nomad's list on a PC. <paramref name="State"/> is <c>waiting</c> (a card waits on the user), <c>working</c>,
/// <c>idle</c> (its process runs), <c>stopped</c>, or <c>closed</c>: in the PC's Idle list and not open there (no last line);
/// the phone can read its history, and input to it (a prompt, a setting) opens it on the PC, as clicking its row does. <paramref name="Waiting"/> says what waits, in a few words.
/// </summary>
public sealed record BoardChat(
    string Backend,
    string SessionId,
    string Title,
    string Agent,
    string? Folder,
    string State,
    string? Waiting,
    string LastLine,
    DateTime LastActivityUtc,
    int Queued);

/// <summary>
/// A PC on the board and the chats open in its Nomad; no chats when it has gone. <paramref name="Seq"/> grows with every change
/// on the board, so a watcher keeps a PC's entry only when its sequence is newer than the one it has. <paramref name="Online"/>:
/// the PC is connected and lists its chats, even with none open (a new chat can start there); false once it has gone.
/// </summary>
public sealed record BoardDevice(Guid DeviceId, string DeviceName, IReadOnlyList<BoardChat> Chats, long Seq = 0, bool Online = false);

/// <summary>
/// A page of a chat's transcript, oldest first: the entries before the cursor asked for (the newest without one).
/// <paramref name="Total"/> is how many items the transcript has (some are never sent, such as thinking); an entry's
/// <see cref="ChatEntry.Index"/> is its item's position, and the cursor for the page before it. <paramref name="Generation"/>
/// changes whenever items before the end change place (a reload, a rewind): cursors from another generation no longer fit, so
/// a phone that sees it change starts again from the newest page. <paramref name="Loading"/>: the transcript is still loading.
/// </summary>
/// <param name="ThinkingInFull">The PC writes Claude's thinking out in full rather than folding it, so the phone does too.</param>
public sealed record ChatPage(string State, int Queued, int Total, IReadOnlyList<ChatEntry> Entries, int Generation = 0, bool Loading = false,
    bool ThinkingInFull = false);

/// <summary>
/// One transcript entry as the phone shows it. <paramref name="Kind"/> is <c>user</c>, <c>assistant</c>, <c>activity</c>
/// (a group of tool calls), <c>thinking</c>, <c>notice</c>, <c>error</c>, <c>permission</c> or <c>question</c>; the last two
/// carry a card. An activity's <paramref name="Steps"/> are what it did, in order: each command or file with its one line, and
/// the thinking between them, so the phone can show what the agent is doing, not only that it is busy.
/// </summary>
public sealed record ChatEntry(int Index, string Kind, string Text, string? From = null, ChatCard? Card = null, IReadOnlyList<ChatStep>? Steps = null);

/// <summary>
/// One step of an activity: <paramref name="Kind"/> <c>tool</c> (<paramref name="Title"/> the tool's name, <paramref name="Detail"/>
/// its one line, such as the command it runs) or <c>thinking</c> (<paramref name="Detail"/> the thought). <paramref name="Failed"/>:
/// the tool call failed.
/// </summary>
public sealed record ChatStep(string Kind, string Title, string? Detail = null, bool Failed = false);

/// <summary>A permission or question card. <paramref name="Id"/> names it in an answer; only a pending one takes one.</summary>
public sealed record ChatCard(
    string Id,
    bool Pending,
    string? Title,
    string? Detail,
    string? Description,
    bool CanAllowForSession,
    string? AllowText,
    string? DenyText,
    string? Answer,
    IReadOnlyList<ChatQuestion>? Questions);

public sealed record ChatQuestion(string Header, string Question, bool MultiSelect, IReadOnlyList<ChatOption> Options);

public sealed record ChatOption(string Label, string? Description);

/// <summary>
/// Input for a chat: <c>prompt</c> (<paramref name="Text"/>, queued behind a running turn as if typed, with any
/// <paramref name="Files"/> attached as if attached there: an image as an image, another file by its path on the PC), <c>answer</c>
/// (<paramref name="Card"/>, <paramref name="Choice"/> <c>allow</c>, <c>session</c> or <c>deny</c>, and for a question one
/// <see cref="ChatAnswer"/> per question, in order), <c>stop</c>, or a setting changed as in the chat's own menus on the PC:
/// <c>model</c>, <c>effort</c> or <c>mode</c>, <paramref name="Choice"/> the id of one of its <see cref="ChatSettings"/> choices.
/// </summary>
/// <remarks>
/// <paramref name="Id"/>, when given, makes a retry safe: the PC applies an input with an id it has just applied only once, and
/// answers the retry with the first result.
/// </remarks>
public sealed record ChatInput(string Kind, string? Text = null, string? Card = null, string? Choice = null, IReadOnlyList<ChatAnswer>? Answers = null, string? Id = null,
    IReadOnlyList<ChatFile>? Files = null);

/// <summary>A question's answer: the labels picked, and/or typed text.</summary>
public sealed record ChatAnswer(IReadOnlyList<string> Options, string? Other);

public sealed record ChatInputResult(bool Ok, string Message);

/// <summary>One choice in a chat setting's list (a model, an effort, a mode, an agent): its id and what the PC calls it.</summary>
public sealed record ChatChoice(string Id, string Name, string? Description = null);

/// <summary>
/// A chat's settings as its menus on the PC show them: the model, effort and permission mode now, and what each can be
/// changed to (a list with one choice or none: nothing to change).
/// </summary>
/// <param name="Title">The chat's name in the PC's list, which input of kind <c>rename</c> changes; null from an older PC.</param>
/// <param name="Pinned">Whether it is pinned in the PC's list (input <c>pin</c>, Choice <c>on</c> or <c>off</c>); null from an
/// older PC. Input <c>archive</c> archives it there, ending its process, as the list's Archive does.</param>
public sealed record ChatSettings(
    string? Model,
    IReadOnlyList<ChatChoice> Models,
    string? Effort,
    IReadOnlyList<ChatChoice> Efforts,
    string? Mode,
    IReadOnlyList<ChatChoice> Modes,
    string? Title = null,
    bool? Pinned = null);

/// <summary>
/// What a new chat on a PC can start as: each agent with its settings as a new chat there starts with them (the defaults
/// chosen), the agent picked last, and the folders used lately (the first the one picked last).
/// </summary>
public sealed record NewChatOptions(IReadOnlyList<NewChatAgent> Agents, string DefaultAgent, IReadOnlyList<string> Folders);

public sealed record NewChatAgent(string Id, string Name, ChatSettings Settings);

/// <summary>
/// A chat to start on a PC with its first prompt: <paramref name="Folder"/> null for none; a null setting takes the PC's default.
/// <paramref name="Id"/>, when given, makes a retry safe, as for <see cref="ChatInput"/>.
/// </summary>
public sealed record NewChat(string Backend, string? Folder, string? Model, string? Effort, string? Mode, string Prompt, string? Id = null);

/// <summary>Whether the chat started, and its session id once it has one (the PC waits a little for it).</summary>
public sealed record NewChatResult(bool Ok, string Message, string? SessionId = null);

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

    // The phone (docs/mobile.md).

    /// <summary>Client → server (a PC): <c>(IReadOnlyList&lt;BoardChat&gt;)</c>, the chats open in it now, all of them.</summary>
    public const string BoardPublish = "BoardPublish";

    /// <summary>Client → server (a PC): no arguments, it stopped sharing its chats: off the board, so it shows offline to phones
    /// (an empty <see cref="BoardPublish"/> means sharing with nothing open).</summary>
    public const string BoardWithdraw = "BoardWithdraw";

    /// <summary>Client → server (the phone): → <c>IReadOnlyList&lt;BoardDevice&gt;</c>, and <see cref="BoardChanged"/> from then on.</summary>
    public const string BoardWatch = "BoardWatch";

    /// <summary>Server → board watchers: <c>(BoardDevice)</c>, a PC's chats changed (none: it went).</summary>
    public const string BoardChanged = "BoardChanged";

    /// <summary>Client → server (the phone): <c>(Guid device, backend, sessionId, int? before, int limit)</c> → <see cref="ChatPage"/>.</summary>
    public const string ChatHistory = "ChatHistory";

    /// <summary>Server → the PC with the chat, a client result: <c>(backend, sessionId, int? before, int limit)</c> → <see cref="ChatPage"/>.</summary>
    public const string ChatHistoryRequested = "ChatHistoryRequested";

    /// <summary>Client → server (the phone): <c>(Guid device, backend, sessionId, ChatInput)</c> → <see cref="ChatInputResult"/>.</summary>
    public const string ChatSend = "ChatSend";

    /// <summary>Server → the PC with the chat, a client result: <c>(backend, sessionId, ChatInput, string fromName)</c> → <see cref="ChatInputResult"/>.</summary>
    public const string ChatInputRequested = "ChatInputRequested";

    /// <summary>Client → server (the phone): <c>(Guid device, backend, sessionId)</c> → <see cref="ChatSettings"/>.</summary>
    public const string ChatSettingsGet = "ChatSettingsGet";

    /// <summary>Server → the PC with the chat, a client result: <c>(backend, sessionId)</c> → <see cref="ChatSettings"/>.</summary>
    public const string ChatSettingsRequested = "ChatSettingsRequested";

    /// <summary>Client → server (the phone): <c>(Guid device)</c> → <see cref="NewChatOptions"/>, what a new chat there can be.</summary>
    public const string NewChatOptionsGet = "NewChatOptionsGet";

    /// <summary>Server → the PC, a client result: <c>()</c> → <see cref="NewChatOptions"/>.</summary>
    public const string NewChatOptionsRequested = "NewChatOptionsRequested";

    /// <summary>Client → server (the phone): <c>(Guid device, NewChat)</c> → <see cref="NewChatResult"/>.</summary>
    public const string ChatStart = "ChatStart";

    /// <summary>Server → the PC, a client result: <c>(NewChat, string fromName)</c> → <see cref="NewChatResult"/>.</summary>
    public const string ChatStartRequested = "ChatStartRequested";

    /// <summary>How long the hub waits for a PC to answer for its chat.</summary>
    public static readonly TimeSpan ChatAnswerTimeout = TimeSpan.FromSeconds(15);

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
