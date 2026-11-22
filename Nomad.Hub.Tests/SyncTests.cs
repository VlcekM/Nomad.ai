using System.Net.Http.Json;
using Nomad.Core.Agents;
using Nomad.Core.Hub;
using Nomad.Core.Remote;
using Nomad.Core.Sessions;
using Nomad.Hub.Contracts;

namespace Nomad.Hub.Tests;

/// <summary>Two PCs (two Claude Code project roots, the same repository in different folders) syncing through one hub.</summary>
public sealed class SyncTests : IDisposable
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    private readonly HubFactory _hub = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomad-sync-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _hub.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed record Pc(string Folder, SessionStore Store, HubSync Sync, List<PulledSession> Pulled, HttpClient Http)
    {
        public string? Transcript(string id = Id) => Store.FindTranscript(id);
    }

    private async Task<Pc> PcAsync(string name)
    {
        var folder = Path.Combine(_root, name, "work", "repo");
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        File.WriteAllText(Path.Combine(folder, ".git", "config"), "[remote \"origin\"]\n\turl = git@github.com:Me/Repo.git\n");
        var store = new SessionStore(Path.Combine(_root, name, "projects"));
        var http = await _hub.EnrollAsync(name);
        var client = new HubClient(_hub.Server.BaseAddress, http.DefaultRequestHeaders.Authorization!.Parameter!, _hub.Server.CreateHandler());
        var sync = new HubSync(client, [new ClaudeCodeFiles(store)], Path.Combine(_root, name, "hub-sync.json"), (_, _) => false);
        var pulled = new List<PulledSession>();
        sync.SessionPulled += pulled.Add;
        return new Pc(folder, store, sync, pulled, http);
    }

    private static string Line(string folder, string type, string text, int n, string? messageId = null)
    {
        var cwd = folder.Replace("\\", "\\\\");
        var at = $"2026-10-30T10:00:{n:00}.000Z";
        return type == "user"
            ? $$$"""{"type":"user","message":{"role":"user","content":"{{{text}}}"},"uuid":"u{{{n}}}","timestamp":"{{{at}}}","cwd":"{{{cwd}}}","sessionId":"{{{Id}}}"}"""
            : $$$"""{"type":"assistant","message":{"id":"{{{messageId}}}","model":"claude-opus-5-5","role":"assistant","content":[{"type":"text","text":"{{{text}}}"}],"usage":{"input_tokens":10,"output_tokens":{{{n}}},"cache_read_input_tokens":100,"cache_creation_input_tokens":5}},"uuid":"a{{{n}}}","timestamp":"{{{at}}}","cwd":"{{{cwd}}}","sessionId":"{{{Id}}}"}""";
    }

    private static void Append(string file, params string[] lines)
    {
        File.AppendAllLines(file, lines);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000))); // Stamps must move even within a tick.
    }

    [Fact]
    public async Task SessionsTravelBetweenPcsAndDivergedCopiesFork()
    {
        var a = await PcAsync("A");
        var b = await PcAsync("B");

        // A has a session with a subagent's transcript beside it.
        var dir = Path.Combine(a.Store.ProjectsRoot, RemoteSessionScanner.ProjectDirectoryName(a.Folder));
        Directory.CreateDirectory(Path.Combine(dir, Id, "subagents"));
        var transcriptA = Path.Combine(dir, Id + ".jsonl");
        Append(transcriptA, Line(a.Folder, "user", "Fix the login test", 1), Line(a.Folder, "assistant", "Done.", 2, "msg_1"));
        File.WriteAllText(Path.Combine(dir, Id, "subagents", "agent-1.jsonl"), "{}\n");
        await a.Sync.SyncNowAsync();

        // B keeps the repository elsewhere: it maps the project to its own folder before its first fetch.
        var project = Assert.Single((await b.Http.GetFromJsonAsync("/api/projects", HubJson.Default.ListProjectDto))!);
        Assert.Equal("github.com/me/repo", project.Key);
        await b.Http.PutAsJsonAsync($"/api/projects/{project.Id}/folder", new ProjectFolderRequest(b.Folder), HubJson.Default.ProjectFolderRequest);
        await b.Sync.SyncNowAsync();

        var pulled = Assert.Single(b.Pulled);
        Assert.Equal((b.Folder, a.Folder, true), (pulled.Folder, pulled.OriginalFolder, pulled.IsNew));
        var transcriptB = b.Transcript()!;
        Assert.Equal(Path.Combine(b.Store.ProjectsRoot, RemoteSessionScanner.ProjectDirectoryName(b.Folder), Id + ".jsonl"), transcriptB);
        Assert.Equal(File.ReadAllText(transcriptA), File.ReadAllText(transcriptB));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(transcriptB)!, Id, "subagents", "agent-1.jsonl")));

        // B continues it; A gets the new turn (sent as an append).
        Append(transcriptB, Line(b.Folder, "user", "And the logout one", 3), Line(b.Folder, "assistant", "Also done.", 4, "msg_2"));
        await b.Sync.SyncNowAsync();
        await a.Sync.SyncNowAsync();
        Assert.Equal(File.ReadAllText(transcriptB), File.ReadAllText(transcriptA));

        // Nothing to do: no new version.
        var before = (await a.Http.GetFromJsonAsync($"/api/sessions/claude-code/{Id}", HubJson.Default.SessionDto))!.Version;
        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();
        Assert.Equal(before, (await a.Http.GetFromJsonAsync($"/api/sessions/claude-code/{Id}", HubJson.Default.SessionDto))!.Version);

        // Both go on without the other (offline): B uploads first, A's copy is kept as a new session.
        Append(transcriptA, Line(a.Folder, "user", "A's own turn", 5));
        Append(transcriptB, Line(b.Folder, "user", "B's own turn", 6));
        await b.Sync.SyncNowAsync();
        await a.Sync.SyncNowAsync();
        Assert.Equal(File.ReadAllText(transcriptB), File.ReadAllText(transcriptA));
        var fork = Directory.GetFiles(Path.GetDirectoryName(transcriptA)!, "*.jsonl").Single(f => !f.EndsWith(Id + ".jsonl"));
        var forkText = File.ReadAllText(fork);
        Assert.Contains("A's own turn", forkText);
        Assert.DoesNotContain(Id, forkText);
        Assert.Contains("(from ", a.Store.GetSummary(fork)!.Title);

        // Usage: each assistant message once, whoever sent it.
        var report = (await a.Http.GetFromJsonAsync("/api/usage?from=2026-10-01T00:00:00Z&to=2026-11-01T00:00:00Z", HubJson.Default.UsageReport))!;
        Assert.Equal(2, report.Rows.Sum(r => r.Calls));
        Assert.Equal(6, report.Rows.Sum(r => r.Output));
    }

    [Fact]
    public async Task ACopyAheadOfTheHubIsNeverOverwritten()
    {
        var a = await PcAsync("A");
        var b = await PcAsync("B");
        var dirA = Path.Combine(a.Store.ProjectsRoot, RemoteSessionScanner.ProjectDirectoryName(a.Folder));
        Directory.CreateDirectory(dirA);
        var transcriptA = Path.Combine(dirA, Id + ".jsonl");
        Append(transcriptA, Line(a.Folder, "user", "Start", 1));

        // B had the same session from before the hub, with one more turn.
        var dirB = Path.Combine(b.Store.ProjectsRoot, RemoteSessionScanner.ProjectDirectoryName(a.Folder));
        Directory.CreateDirectory(dirB);
        var transcriptB = Path.Combine(dirB, Id + ".jsonl");
        File.Copy(transcriptA, transcriptB);
        Append(transcriptB, Line(a.Folder, "user", "More", 2));

        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync(); // Fetch skipped (B is ahead); upload adds to the hub's.
        Assert.Contains("More", File.ReadAllText(transcriptB));
        await a.Sync.SyncNowAsync();
        Assert.Equal(File.ReadAllText(transcriptB), File.ReadAllText(transcriptA));
    }

    [Fact]
    public async Task CodexRolloutsAndTheirNamesTravel()
    {
        const string thread = "01a18c0e-9dba-71b3-9156-36186f17476f";
        var homes = new[] { "A", "B" }.Select(n => Path.Combine(_root, n, "codex")).ToArray();
        var syncs = new List<HubSync>();
        foreach (var (home, name) in homes.Zip(["A", "B"]))
        {
            Directory.CreateDirectory(home);
            var http = await _hub.EnrollAsync(name);
            var client = new HubClient(_hub.Server.BaseAddress, http.DefaultRequestHeaders.Authorization!.Parameter!, _hub.Server.CreateHandler());
            syncs.Add(new HubSync(client, [new CodexFiles(home)], Path.Combine(_root, name, "hub-sync-codex.json"), (_, _) => false));
        }

        var relative = Path.Combine("sessions", "2026", "10", "30", $"rollout-2026-10-30T10-05-30-{thread}.jsonl");
        var rolloutA = Path.Combine(homes[0], relative);
        Directory.CreateDirectory(Path.GetDirectoryName(rolloutA)!);
        File.WriteAllLines(rolloutA, [
            $$$"""{"timestamp":"2026-10-30T09:05:32.097Z","type":"session_meta","payload":{"id":"{{{thread}}}","cwd":"R:\\Work\\Repo"}}""",
            """{"timestamp":"2026-10-30T09:05:33Z","ordinal":1,"type":"turn_context","payload":{"model":"gpt-6.1-sol"}}""",
            """{"timestamp":"2026-10-30T09:05:34Z","ordinal":2,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":30},"last_token_usage":{"input_tokens":20,"cached_input_tokens":5,"output_tokens":10}}}}""",
        ]);
        File.WriteAllLines(Path.Combine(homes[0], CodexFiles.Index), [$$$"""{"id":"{{{thread}}}","thread_name":"Fix the build","updated_at":"2026-10-30T09:06:00Z"}"""]);
        File.WriteAllLines(Path.Combine(homes[1], CodexFiles.Index), ["""{"id":"other","thread_name":"Something else"}"""]);

        await syncs[0].SyncNowAsync();
        await syncs[1].SyncNowAsync();

        Assert.Equal(File.ReadAllText(rolloutA), File.ReadAllText(Path.Combine(homes[1], relative)));
        var index = File.ReadAllLines(Path.Combine(homes[1], CodexFiles.Index));
        Assert.Equal(2, index.Length);
        Assert.Contains("Fix the build", index[1]);
        Assert.Equal("Fix the build", Assert.Single(new CodexFiles(homes[1]).List(CancellationToken.None), s => s.SessionId == thread).Title);

        // Nothing changes on a second round (the appended index line doesn't count as a change of the session).
        var version = (await syncs[1].Client.SessionAsync("codex", thread))!.Version;
        await syncs[1].SyncNowAsync();
        await syncs[0].SyncNowAsync();
        Assert.Equal(version, (await syncs[1].Client.SessionAsync("codex", thread))!.Version);
    }

    /// <summary>Two PCs' Antigravity homes, each with the chat list Nomad's settings would give it.</summary>
    private async Task<(string Home, List<SessionSummary> Chats, HubSync Sync, List<PulledSession> Pulled)> AntigravityPcAsync(string name)
    {
        var home = Path.Combine(_root, name, "antigravity");
        Directory.CreateDirectory(Path.Combine(home, "conversations"));
        var chats = new List<SessionSummary>();
        var http = await _hub.EnrollAsync(name);
        var client = new HubClient(_hub.Server.BaseAddress, http.DefaultRequestHeaders.Authorization!.Parameter!, _hub.Server.CreateHandler());
        var sync = new HubSync(client, [new AntigravityFiles(home, () => chats.ToList())], Path.Combine(_root, name, "hub-sync-agy.json"), (_, _) => false);
        var pulled = new List<PulledSession>();
        sync.SessionPulled += pulled.Add;
        return (home, chats, sync, pulled);
    }

    private static void Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000))); // Stamps must move even within a tick.
    }

    [Fact]
    public async Task AntigravityChatsTravelWithTheirLogNotesAndTitle()
    {
        const string chat = "c29f4517-0eb5-499e-82dd-6e95a5447b0e";
        var a = await AntigravityPcAsync("A");
        var b = await AntigravityPcAsync("B");
        var folder = Path.Combine(_root, "A", "work");
        a.Chats.Add(new SessionSummary(chat, "", folder, "Pastrami", DateTime.UtcNow, 0, Backend: AgentBackends.Antigravity));
        Write(Path.Combine(a.Home, "conversations", chat + ".db"), "database v1");
        Write(Path.Combine(a.Home, "conversations", chat + ".db-wal"), "log v1");
        Write(Path.Combine(a.Home, "brain", chat, "scratch", "notes.md"), "notes");
        Write(Path.Combine(a.Home, "annotations", chat + ".pbtxt"), "title:\"Pastrami\"");

        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();

        Assert.Equal("database v1", File.ReadAllText(Path.Combine(b.Home, "conversations", chat + ".db")));
        Assert.Equal("log v1", File.ReadAllText(Path.Combine(b.Home, "conversations", chat + ".db-wal")));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(b.Home, "brain", chat, "scratch", "notes.md")));
        Assert.Equal("title:\"Pastrami\"", File.ReadAllText(Path.Combine(b.Home, "annotations", chat + ".pbtxt")));
        var pulled = Assert.Single(b.Pulled);
        Assert.Equal((AgentBackends.Antigravity, chat, "Pastrami", true), (pulled.Backend, pulled.SessionId, pulled.Title, pulled.IsNew));
        b.Chats.Add(new SessionSummary(chat, "", pulled.Folder, pulled.Title, pulled.LastActivityUtc, 0, Backend: AgentBackends.Antigravity));

        // A checkpoints (the log is folded into the database) and renames the chat: B's old log must not survive, or
        // SQLite would replay it into the new database.
        File.Delete(Path.Combine(a.Home, "conversations", chat + ".db-wal"));
        Write(Path.Combine(a.Home, "conversations", chat + ".db"), "database v2");
        a.Chats[0] = a.Chats[0] with { Title = "Pastrami on rye" };
        Write(Path.Combine(b.Home, "conversations", chat + ".db-shm"), "index");

        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();

        Assert.Equal("database v2", File.ReadAllText(Path.Combine(b.Home, "conversations", chat + ".db")));
        Assert.False(File.Exists(Path.Combine(b.Home, "conversations", chat + ".db-wal")));
        Assert.False(File.Exists(Path.Combine(b.Home, "conversations", chat + ".db-shm")));
        Assert.Equal("Pastrami on rye", b.Pulled[^1].Title);
        Assert.Equal("Pastrami on rye", (await b.Sync.Client.SessionAsync(AgentBackends.Antigravity, chat))!.Title);

        // B's list catches up, then B renames it: a rename alone travels.
        b.Chats[0] = b.Chats[0] with { Title = "Pastrami on rye" };
        await b.Sync.SyncNowAsync();
        b.Chats[0] = b.Chats[0] with { Title = "Reuben" };
        await b.Sync.SyncNowAsync();
        await a.Sync.SyncNowAsync();
        Assert.Equal("Reuben", (await a.Sync.Client.SessionAsync(AgentBackends.Antigravity, chat))!.Title);
        Assert.Equal("Reuben", a.Pulled[^1].Title);
    }

    [Fact]
    public async Task AnAntigravityChatChangedOnBothPcsIsSetAsideNotLost()
    {
        const string chat = "0249691e-9698-4760-95a6-5b1bb6355635";
        var a = await AntigravityPcAsync("A");
        var b = await AntigravityPcAsync("B");
        var db = (string home) => Path.Combine(home, "conversations", chat + ".db");
        a.Chats.Add(new SessionSummary(chat, "", Path.Combine(_root, "A", "work"), "Shared", DateTime.UtcNow, 0, Backend: AgentBackends.Antigravity));
        Write(db(a.Home), "database");
        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync(); // B has no annotations folder: a fetch without one must not need it.
        Assert.Single(b.Pulled);
        b.Chats.Add(new SessionSummary(chat, "", b.Pulled[0].Folder, "Shared", DateTime.UtcNow, 0, Backend: AgentBackends.Antigravity));
        await b.Sync.SyncNowAsync();

        // Both add turns while apart. With the CLI keeping the chat open they land in the log, and the database file itself
        // doesn't change, so its first bytes can't tell the copies apart.
        Write(db(a.Home) + "-wal", "A's turns");
        Write(db(b.Home) + "-wal", "B's turns");
        await a.Sync.SyncNowAsync();
        var statuses = new List<string>();
        b.Sync.Status += statuses.Add;
        await b.Sync.SyncNowAsync();

        // B's upload is refused: its copy is kept whole, and A's (the hub's) is used.
        Assert.Equal("A's turns", File.ReadAllText(db(b.Home) + "-wal"));
        var aside = Assert.Single(Directory.GetDirectories(Path.Combine(b.Home, "nomad-set-aside")));
        Assert.Equal("B's turns", File.ReadAllText(Path.Combine(aside, "conversations", chat + ".db-wal")));
        Assert.Contains(statuses, s => s.Contains(aside, StringComparison.Ordinal));

        // And the PCs agree from then on: nothing more is set aside, and A keeps its turns.
        await b.Sync.SyncNowAsync();
        await a.Sync.SyncNowAsync();
        Assert.Single(Directory.GetDirectories(Path.Combine(b.Home, "nomad-set-aside")));
        Assert.Equal("A's turns", File.ReadAllText(db(a.Home) + "-wal"));
    }

    [Fact]
    public async Task AnAntigravityCopyFromBeforeTheHubIsComparedWhole()
    {
        const string chat = "0438bf40-efae-4d22-a6d0-3231a4a2f8d9";
        var a = await AntigravityPcAsync("A");
        var b = await AntigravityPcAsync("B");
        var summary = new SessionSummary(chat, "", Path.Combine(_root, "A", "work"), "Copied", DateTime.UtcNow, 0, Backend: AgentBackends.Antigravity);
        a.Chats.Add(summary);
        b.Chats.Add(summary);
        Write(Path.Combine(a.Home, "conversations", chat + ".db"), "same");
        File.WriteAllText(Path.Combine(_root, "same.db"), "same");
        Directory.CreateDirectory(Path.Combine(b.Home, "conversations"));
        File.Copy(Path.Combine(a.Home, "conversations", chat + ".db"), Path.Combine(b.Home, "conversations", chat + ".db"));

        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();

        // The same chat on both: nothing is set aside, fetched or uploaded again.
        Assert.False(Directory.Exists(Path.Combine(b.Home, "nomad-set-aside")));
        Assert.Empty(b.Pulled);
        Assert.Equal(1, (await b.Sync.Client.SessionAsync(AgentBackends.Antigravity, chat))!.Version);
    }

    [Fact]
    public async Task ReadingAnAntigravityChatIsNotAChange()
    {
        const string chat = "055bc3b1-1c70-4de8-8425-3e8dfb992206";
        var a = await AntigravityPcAsync("A");
        a.Chats.Add(new SessionSummary(chat, "", null, "Read only", DateTime.UtcNow, 0, Backend: AgentBackends.Antigravity));
        Write(Path.Combine(a.Home, "conversations", chat + ".db"), "database");
        await a.Sync.SyncNowAsync();

        Write(Path.Combine(a.Home, "conversations", chat + ".db-wal"), ""); // What the CLI leaves after loading a chat.
        await a.Sync.SyncNowAsync();

        Assert.Equal(1, (await a.Sync.Client.SessionAsync(AgentBackends.Antigravity, chat))!.Version);
    }

    [Fact]
    public async Task AntigravityNotesDeletedAtTheSourceGoEverywhere()
    {
        const string chat = "07a66e40-6da8-4130-b23b-dfbd9100de95";
        var a = await AntigravityPcAsync("A");
        var b = await AntigravityPcAsync("B");
        a.Chats.Add(new SessionSummary(chat, "", null, "Notes", DateTime.UtcNow, 0, Backend: AgentBackends.Antigravity));
        Write(Path.Combine(a.Home, "conversations", chat + ".db"), "v1");
        Write(Path.Combine(a.Home, "brain", chat, "keep.md"), "keep");
        Write(Path.Combine(a.Home, "brain", chat, "drop.md"), "drop");
        Write(Path.Combine(a.Home, "annotations", chat + ".pbtxt"), "title");
        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();
        b.Chats.Add(a.Chats[0]);
        Assert.True(File.Exists(Path.Combine(b.Home, "brain", chat, "drop.md")));

        File.Delete(Path.Combine(a.Home, "brain", chat, "drop.md"));
        File.Delete(Path.Combine(a.Home, "annotations", chat + ".pbtxt"));
        Write(Path.Combine(a.Home, "conversations", chat + ".db"), "v2");
        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();

        Assert.Equal("v2", File.ReadAllText(Path.Combine(b.Home, "conversations", chat + ".db")));
        Assert.True(File.Exists(Path.Combine(b.Home, "brain", chat, "keep.md")));
        Assert.False(File.Exists(Path.Combine(b.Home, "brain", chat, "drop.md")));
        Assert.False(File.Exists(Path.Combine(b.Home, "annotations", chat + ".pbtxt")));
    }

    [Fact]
    public async Task AnAntigravityChatIdIsNeverAPathNorListed()
    {
        var home = Path.Combine(_root, "C", "antigravity");
        var odd = new[] { "..\\..\\evil", "{0249691E-9698-4760-95A6-5B1BB6355635}", "0249691e96984760 95a65b1bb6355635" };
        var files = new AntigravityFiles(home, () => [.. odd.Select(id => new SessionSummary(id, "", null, "x", DateTime.UtcNow, 0, Backend: AgentBackends.Antigravity))]);
        var download = Path.Combine(_root, "download.db");
        Write(download, "x");

        Assert.Empty(files.List(CancellationToken.None));
        foreach (var id in odd)
        {
            var remote = new SessionDto(AgentBackends.Antigravity, id, null, null, "x", null, null, DateTime.UtcNow, 1, 1, [], Guid.Empty, 1, false, null, null);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                files.WriteAsync(remote, new Dictionary<string, string> { [AntigravityFiles.Database] = download }, _root, CancellationToken.None));
        }

        Assert.False(Directory.Exists(home));
    }
}
