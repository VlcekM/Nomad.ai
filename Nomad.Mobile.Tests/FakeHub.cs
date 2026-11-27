using Nomad.Hub.Contracts;
using Nomad.Mobile.Hub;

namespace Nomad.Mobile.Tests;

/// <summary>A hub in memory: a board, pages per chat, and a record of every call, with its events raised on demand.</summary>
public sealed class FakeHub : IHubConnection, IHubFactory
{
    public List<BoardDevice> Board { get; } = [];

    public Dictionary<string, ChatPage> Pages { get; } = [];

    public List<(Guid Device, string Backend, string Session, ChatInput Input)> Sent { get; } = [];

    public List<string> Calls { get; } = [];

    public List<Guid> Revoked { get; } = [];

    /// <summary>What the PC answers to input; "Sent." by default.</summary>
    public Func<ChatInput, ChatInputResult> Answer { get; set; } = _ => new ChatInputResult(true, "Sent.");

    public WatchResult Watch { get; set; } = new(true, "Desk PC", [], 0);

    public bool IsConnected { get; private set; }

    public event Action<bool>? ConnectionChanged;

    public event Action<BoardDevice>? BoardChanged;

    public event Action<string, string, string[], long>? RelayEvents;

    public event Action<string, string>? RelayEnded;

    public event Action<Exception>? ConnectFailed;

    /// <summary>The hub refuses this phone (revoked, say): connecting fails with this, as the real client reports it.</summary>
    public Exception? Refusal { get; set; }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        Calls.Add("Connect");
        return TryConnect();
    }

    public Task ReconnectNowAsync(CancellationToken ct = default)
    {
        Calls.Add("ReconnectNow");
        return TryConnect();
    }

    private Task TryConnect()
    {
        if (Refusal is { } refusal)
        {
            ConnectFailed?.Invoke(refusal);
        }
        else if (!IsConnected)
        {
            SetConnected(true);
        }

        return Task.CompletedTask;
    }

    public void SetConnected(bool up)
    {
        IsConnected = up;
        ConnectionChanged?.Invoke(up);
    }

    public void RaiseBoard(BoardDevice device) => BoardChanged?.Invoke(device);

    public void RaiseRelay(string backend, string session, long first, params string[] events) => RelayEvents?.Invoke(backend, session, events, first);

    public void RaiseRelayEnded(string backend, string session) => RelayEnded?.Invoke(backend, session);

    public Task<IReadOnlyList<BoardDevice>> BoardWatchAsync(CancellationToken ct = default)
    {
        Calls.Add("BoardWatch");
        return Task.FromResult<IReadOnlyList<BoardDevice>>([.. Board]);
    }

    public Task<ChatPage> ChatHistoryAsync(Guid device, string backend, string sessionId, int? before, int limit, CancellationToken ct = default)
    {
        Calls.Add($"History {sessionId} {before?.ToString() ?? "newest"}");
        var page = Pages.TryGetValue(sessionId, out var p) ? p : new ChatPage("idle", 0, 0, []);
        var entries = page.Entries.Where(e => before is null || e.Index < before).TakeLast(limit).ToList();
        return Task.FromResult(page with { Entries = entries });
    }

    public Task<ChatInputResult> ChatSendAsync(Guid device, string backend, string sessionId, ChatInput input, CancellationToken ct = default)
    {
        Sent.Add((device, backend, sessionId, input));
        return Task.FromResult(Answer(input));
    }

    /// <summary>What the PC says a chat's settings are; a Claude Code chat on Opus by default.</summary>
    public ChatSettings Settings { get; set; } = new("opus", [new("opus", "Opus"), new("sonnet", "Sonnet")], "high",
        [new("low", "Low"), new("high", "High")], "default", [new("default", "Manual"), new("plan", "Plan")]);

    /// <summary>What a new chat on a PC can be: Claude Code and Codex, two folders.</summary>
    public NewChatOptions NewChatOptions { get; set; } = new(
        [new("claude-code", "Claude Code", new("opus", [new("opus", "Opus"), new("sonnet", "Sonnet")], "high", [new("low", "Low"), new("high", "High")],
            "default", [new("default", "Manual"), new("plan", "Plan")])),
         new("codex", "Codex", new("gpt", [new("gpt", "GPT")], "", [], "default", [new("default", "Manual")]))],
        "claude-code", [@"E:\Projects\Nomad.ai", @"E:\Projects\Other"]);

    public List<(Guid Device, NewChat Chat)> Started { get; } = [];

    /// <summary>What the PC answers to a new chat; started, with a session id, by default.</summary>
    public Func<NewChat, NewChatResult> StartAnswer { get; set; } = _ => new NewChatResult(true, "Started.", "new-1");

    public Task<ChatSettings> ChatSettingsAsync(Guid device, string backend, string sessionId, CancellationToken ct = default)
    {
        Calls.Add($"Settings {sessionId}");
        return Task.FromResult(Settings);
    }

    public Task<NewChatOptions> NewChatOptionsAsync(Guid device, CancellationToken ct = default)
    {
        Calls.Add("NewChatOptions");
        return Task.FromResult(NewChatOptions);
    }

    public Task<NewChatResult> ChatStartAsync(Guid device, NewChat chat, CancellationToken ct = default)
    {
        Started.Add((device, chat));
        return Task.FromResult(StartAnswer(chat));
    }

    public Task<WatchResult> WatchAsync(string backend, string sessionId, CancellationToken ct = default)
    {
        Calls.Add($"Watch {sessionId}");
        return Task.FromResult(Watch);
    }

    public Task UnwatchAsync(string backend, string sessionId, CancellationToken ct = default)
    {
        Calls.Add($"Unwatch {sessionId}");
        return Task.CompletedTask;
    }

    public Task RevokeDeviceAsync(Guid id, CancellationToken ct = default)
    {
        Revoked.Add(id);
        return Task.CompletedTask;
    }

    /// <summary>The phone app the hub offers (null: none), and its file's bytes.</summary>
    public PhoneAppInfo? PhoneApp { get; set; }

    public byte[] PhoneAppBytes { get; set; } = [];

    public Task<PhoneAppInfo?> PhoneAppAsync(CancellationToken ct = default)
    {
        Calls.Add("PhoneApp");
        return Task.FromResult(PhoneApp);
    }

    public Task<Stream> PhoneAppDownloadAsync(CancellationToken ct = default)
    {
        Calls.Add("PhoneAppDownload");
        return Task.FromResult<Stream>(new MemoryStream(PhoneAppBytes));
    }

    /// <summary>Files put on the hub, by id, with their bytes; set <see cref="UploadFails"/> to refuse them.</summary>
    public Dictionary<string, (string Name, byte[] Bytes)> Files { get; } = [];

    public bool UploadFails { get; set; }

    public async Task<ChatFile> UploadFileAsync(Stream content, string name, long? length, CancellationToken ct = default)
    {
        Calls.Add("Upload " + name);
        if (UploadFails)
        {
            throw new HttpRequestException("The file couldn't be put on the hub: it is too large.");
        }

        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        var id = Guid.NewGuid().ToString("N");
        Files[id] = (name, copy.ToArray());
        return new ChatFile(id, name, copy.Length);
    }

    public ValueTask DisposeAsync()
    {
        Calls.Add("Dispose");
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ the factory side

    public Guid EnrolledId { get; } = Guid.NewGuid();

    public string? EnrolledToken { get; private set; }

    public Task<EnrollResponse> EnrollAsync(Uri hub, string token, string deviceName, CancellationToken ct = default)
    {
        if (token == "bad")
        {
            throw new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized);
        }

        EnrolledToken = token;
        return Task.FromResult(new EnrollResponse(EnrolledId, "secret-" + token, "test"));
    }

    public IHubConnection Open(Uri hub, string secret)
    {
        Calls.Add("Open " + hub);
        return this;
    }
}

/// <summary>Sample board data.</summary>
public static class Samples
{
    public static readonly Guid Desk = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Laptop = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static BoardChat Chat(string id, string title, string state, int minutesAgo, string? waiting = null, string agent = "Claude Code",
        string folder = @"C:\Work\Nomad.ai", string last = "Done: the tests pass.") =>
        new("claude-code", id, title, agent, folder, state, waiting, last, DateTime.UtcNow.AddMinutes(-minutesAgo), 0);

    public static List<BoardDevice> Board() =>
    [
        new(Laptop, "Laptop", [Chat("l1", "Write the release notes", "idle", 50, last: "Here's a draft of the notes for v2.9.")], 3),
        new(Desk, "Desk PC",
        [
            Chat("d1", "Fix the parser bug", "idle", 2),
            Chat("d2", "Refactor the hub client", "working", 5, last: "Running the hub tests…"),
            Chat("d3", "Update the README", "waiting", 30, waiting: "allow Bash", agent: "Codex", folder: "/home/me/notes"),
            Chat("d4", "Old experiment", "stopped", 600, last: "I stopped here."),
            Chat("d5", "Plan the phone app", "closed", 1500, last: ""),
            Chat("d6", "Tidy the docs", "closed", 3000, last: "", folder: @"C:\Work\docs"),
        ], 7),
    ];

    public static ChatPage Page(bool withCard = false, bool question = false, string state = "idle")
    {
        List<ChatEntry> entries =
        [
            new(0, "user", "The parser chokes on curly apostrophes. Can you find out why?"),
            new(1, "activity", "Read Parser.cs, Searched for 'apostrophe'", Steps:
            [
                new ChatStep("thinking", "Thought", "The report says curly apostrophes, so the lexer's quote handling is the first suspect."),
                new ChatStep("tool", "Read", "src/Parser/Lexer.cs"),
                new ChatStep("tool", "Grep", "apostrophe|\\u2019 in src/"),
                new ChatStep("tool", "Bash", "dotnet test Parser.Tests --filter Quote", Failed: true),
            ]),
            new(2, "assistant", "Found it. **The lexer** treats `’` as the end of a word, so:\n\n1. The token splits in two.\n2. The parser sees a stray quote.\n\n```csharp\nif (c == '\\'') return Token.Quote;\n```\n\nI'll fix the lexer and add a test."),
            new(3, "user", "Go ahead.", From: "Pixel 8"),
            new(4, "notice", "Context compacted"),
        ];
        if (withCard)
        {
            entries.Add(question
                ? new(5, "question", "", Card: new ChatCard("q-1", true, null, null, null, false, null, null, null,
                    [new ChatQuestion("Fix", "Where should the fix go?", false, [new ChatOption("Lexer", "Treat ’ as part of the word"), new ChatOption("Parser", "Skip stray quotes")])]))
                : new(5, "permission", "", Card: new ChatCard("p-1", true, "Allow Bash?", "dotnet test Nomad.Core.Tests", "Run the tests", true, null, null, null, null)));
        }

        return new ChatPage(withCard ? "waiting" : state, 0, entries.Count, entries, Generation: 1);
    }
}
