using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nomad.Core.Agents;
using Nomad.Core.Remote;
using Nomad.Core.Sessions;
using Nomad.Hub.Contracts;

namespace Nomad.Core.Hub;

/// <summary>A session on this PC, with a stamp (sizes and write times) that changes whenever its files do.</summary>
public sealed record LocalSession(
    string Backend,
    string SessionId,
    string Title,
    string? WorkingDirectory,
    string? GitBranch,
    DateTime LastActivityUtc,
    string Stamp);

/// <summary>One file of a session's payload: its name in the payload and where it is (or was exported to) on disk.</summary>
public sealed record PayloadFile(string Path, string FullPath);

/// <summary>
/// How one backend's sessions become files for the hub and back (docs/hub.md §4). The sync loop only sees these.
/// </summary>
public interface ISessionFiles
{
    string Backend { get; }

    IReadOnlyList<LocalSession> List(CancellationToken ct);

    /// <summary>The session's files as they are now; temporary ones (an export) are deleted by <see cref="Release"/>.</summary>
    Task<IReadOnlyList<PayloadFile>> CollectAsync(LocalSession session, CancellationToken ct);

    void Release(IReadOnlyList<PayloadFile> files);

    /// <summary>
    /// Puts a session fetched from the hub on this PC: <paramref name="files"/> are downloaded copies of its payload,
    /// <paramref name="folder"/> the folder it belongs to here. Returns the session's stamp afterwards.
    /// </summary>
    Task<string?> WriteAsync(SessionDto remote, IReadOnlyDictionary<string, string> files, string folder, CancellationToken ct);

    /// <summary>The current stamp of a session, null when it isn't on this PC.</summary>
    string? StampOf(string sessionId);

    /// <summary>
    /// The file that only grows (a transcript, a rollout), the first of the payload; null when there is none (an
    /// OpenCode import merges, so it can't lose anything).
    /// </summary>
    string? MainFile(string sessionId);

    /// <summary>
    /// Copies can't be compared or merged (a database): a copy that changed here and on another PC is set aside with
    /// <see cref="SetAsideAsync"/> and the hub's version used, instead of one being taken for an earlier state of the other.
    /// </summary>
    bool Opaque => false;

    /// <summary>Keeps this PC's copy of a session somewhere safe before an <see cref="Opaque"/> one is replaced; returns where.</summary>
    Task<string?> SetAsideAsync(string sessionId, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Whether a process has the session's files open here, so nothing may replace them (only known for some backends).</summary>
    bool InUse(string sessionId) => false;
}

internal static class Stamps
{
    public static string Of(IEnumerable<string> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(f);
            if (info.Exists)
            {
                sb.Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Puts a fetched file in place, dated when the session was last active: the session list reads its age from the
    /// file's time, so a fetch must not make it look just used.
    /// </summary>
    public static void Copy(string from, string to, DateTime lastActivityUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        var temp = to + ".nomad-hub";
        File.Copy(from, temp, overwrite: true);
        if (lastActivityUtc > DateTime.UnixEpoch && lastActivityUtc <= DateTime.UtcNow)
        {
            File.SetLastWriteTimeUtc(temp, lastActivityUtc);
        }

        File.Move(temp, to, overwrite: true);
    }
}

/// <summary>
/// Claude Code: <c>~/.claude/projects/&lt;folder&gt;/&lt;id&gt;.jsonl</c> as <c>session.jsonl</c>, and everything in the
/// session's own folder beside it (subagents, workflows, tool results, its title) under <c>files/</c>.
/// </summary>
public sealed class ClaudeCodeFiles(SessionStore store) : ISessionFiles
{
    public const string Main = "session.jsonl";

    public string Backend => AgentBackends.ClaudeCode;

    private static IEnumerable<string> Extras(string transcript)
    {
        var dir = Path.Combine(Path.GetDirectoryName(transcript)!, Path.GetFileNameWithoutExtension(transcript));
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) : [];
    }

    public IReadOnlyList<LocalSession> List(CancellationToken ct) =>
        store.Scan(ct).Select(s => new LocalSession(Backend, s.SessionId, s.Title, s.WorkingDirectory, s.GitBranch, s.LastActivityUtc,
            Stamps.Of(Extras(s.FilePath).Prepend(s.FilePath)))).ToList();

    public string? StampOf(string sessionId) =>
        store.FindTranscript(sessionId) is { } file ? Stamps.Of(Extras(file).Prepend(file)) : null;

    public string? MainFile(string sessionId) => store.FindTranscript(sessionId);

    public Task<IReadOnlyList<PayloadFile>> CollectAsync(LocalSession session, CancellationToken ct)
    {
        if (store.FindTranscript(session.SessionId) is not { } file)
        {
            return Task.FromResult<IReadOnlyList<PayloadFile>>([]);
        }

        var dir = Path.Combine(Path.GetDirectoryName(file)!, session.SessionId);
        var files = new List<PayloadFile> { new(Main, file) };
        files.AddRange(Extras(file).Select(f => new PayloadFile("files/" + Path.GetRelativePath(dir, f).Replace('\\', '/'), f)));
        return Task.FromResult<IReadOnlyList<PayloadFile>>(files);
    }

    public void Release(IReadOnlyList<PayloadFile> files)
    {
    }

    public Task<string?> WriteAsync(SessionDto remote, IReadOnlyDictionary<string, string> files, string folder, CancellationToken ct)
    {
        // Where the session already is, else under this PC's folder for it (where --resume looks).
        var transcript = store.FindTranscript(remote.SessionId)
            ?? Path.Combine(store.ProjectsRoot, RemoteSessionScanner.ProjectDirectoryName(folder), remote.SessionId + ".jsonl");
        var dir = Path.Combine(Path.GetDirectoryName(transcript)!, remote.SessionId);
        foreach (var (path, downloaded) in files)
        {
            var target = path == Main ? transcript
                : path.StartsWith("files/", StringComparison.Ordinal) ? Path.GetFullPath(Path.Combine(dir, path["files/".Length..]))
                : null;
            if (target is null || !(target == transcript || target.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // Never outside the session's own files.
            }

            Stamps.Copy(downloaded, target, remote.LastActivityUtc);
        }

        return Task.FromResult(StampOf(remote.SessionId));
    }
}

/// <summary>
/// Codex: the rollout file under its own relative path (<c>sessions/YYYY/MM/DD/rollout-…-&lt;id&gt;.jsonl</c>), and the
/// thread's lines from <c>session_index.jsonl</c> (its name). Codex indexes a rollout dropped into place by itself.
/// </summary>
public sealed class CodexFiles(string codexHome) : ISessionFiles
{
    public const string Index = "session_index.jsonl";

    public string Backend => AgentBackends.Codex;

    private IEnumerable<string> Rollouts()
    {
        foreach (var sub in new[] { "sessions", "archived_sessions" })
        {
            var dir = Path.Combine(codexHome, sub);
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "rollout-*.jsonl", SearchOption.AllDirectories))
                {
                    yield return f;
                }
            }
        }
    }

    internal static string? IdOf(string rollout)
    {
        var name = Path.GetFileNameWithoutExtension(rollout);
        return name.Length > 36 && Guid.TryParse(name[^36..], out _) ? name[^36..] : null;
    }

    private Dictionary<string, string> Names()
    {
        var names = new Dictionary<string, string>();
        foreach (var line in IndexLines())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out var id) && doc.RootElement.TryGetProperty("thread_name", out var name))
                {
                    names[id.GetString()!] = name.GetString() ?? "";
                }
            }
            catch (JsonException)
            {
            }
        }

        return names;
    }

    private IEnumerable<string> IndexLines()
    {
        var path = Path.Combine(codexHome, Index);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static string? CwdOf(string rollout)
    {
        try
        {
            using var stream = new FileStream(rollout, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            if (reader.ReadLine() is { } first)
            {
                using var doc = JsonDocument.Parse(first);
                if (doc.RootElement.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("cwd", out var cwd))
                {
                    return cwd.GetString();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return null;
    }

    public IReadOnlyList<LocalSession> List(CancellationToken ct)
    {
        var names = Names();
        var result = new List<LocalSession>();
        foreach (var rollout in Rollouts())
        {
            ct.ThrowIfCancellationRequested();
            if (IdOf(rollout) is not { } id)
            {
                continue;
            }

            var name = names.GetValueOrDefault(id);
            result.Add(new LocalSession(Backend, id, string.IsNullOrWhiteSpace(name) ? "Codex session" : name, CwdOf(rollout), null,
                File.GetLastWriteTimeUtc(rollout), Stamps.Of([rollout]) + (name ?? "")));
        }

        return result;
    }

    public string? StampOf(string sessionId)
    {
        var rollout = Rollouts().FirstOrDefault(r => IdOf(r) == sessionId);
        return rollout is null ? null : Stamps.Of([rollout]) + (Names().GetValueOrDefault(sessionId) ?? "");
    }

    public string? MainFile(string sessionId) => Rollouts().FirstOrDefault(r => IdOf(r) == sessionId);

    public Task<IReadOnlyList<PayloadFile>> CollectAsync(LocalSession session, CancellationToken ct)
    {
        var rollout = Rollouts().FirstOrDefault(r => IdOf(r) == session.SessionId);
        if (rollout is null)
        {
            return Task.FromResult<IReadOnlyList<PayloadFile>>([]);
        }

        var files = new List<PayloadFile> { new(Path.GetRelativePath(codexHome, rollout).Replace('\\', '/'), rollout) };
        var lines = IndexLines().Where(l => l.Contains(session.SessionId, StringComparison.Ordinal)).ToList();
        if (lines.Count > 0)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"nomad-codex-index-{Guid.NewGuid():N}.jsonl");
            File.WriteAllLines(temp, lines);
            files.Add(new PayloadFile(Index, temp));
        }

        return Task.FromResult<IReadOnlyList<PayloadFile>>(files);
    }

    public void Release(IReadOnlyList<PayloadFile> files)
    {
        foreach (var f in files.Where(f => f.Path == Index))
        {
            File.Delete(f.FullPath);
        }
    }

    public Task<string?> WriteAsync(SessionDto remote, IReadOnlyDictionary<string, string> files, string folder, CancellationToken ct)
    {
        foreach (var (path, downloaded) in files)
        {
            if (path == Index)
            {
                var have = IndexLines().ToHashSet(StringComparer.Ordinal);
                var add = File.ReadAllLines(downloaded).Where(l => l.Length > 0 && !have.Contains(l)).ToList();
                if (add.Count > 0)
                {
                    File.AppendAllLines(Path.Combine(codexHome, Index), add);
                }

                continue;
            }

            var target = Path.GetFullPath(Path.Combine(codexHome, path));
            var inside = target.StartsWith(Path.Combine(codexHome, "sessions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(Path.Combine(codexHome, "archived_sessions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (inside && IdOf(target) == remote.SessionId)
            {
                Stamps.Copy(downloaded, target, remote.LastActivityUtc);
            }
        }

        return Task.FromResult(StampOf(remote.SessionId));
    }
}

/// <summary>
/// Antigravity keeps each chat as its own SQLite database, <c>conversations/&lt;id&gt;.db</c> (with a <c>-wal</c> log beside it
/// while the CLI has it open), plus <c>brain/&lt;id&gt;/</c> (its working notes) and <c>annotations/&lt;id&gt;.pbtxt</c> (its title in the
/// CLI). A chat copied into another PC's home is resumed by the CLI as it is: its own index (<c>conversation_summaries.db</c>)
/// adds the chat on first use. Nomad lists Antigravity chats from its settings, so <paramref name="sessions"/> says which
/// chats there are, with their folders and titles.
/// </summary>
/// <remarks>
/// A chat travels as a snapshot taken with SQLite's backup API: one file holding the database with its log folded in. The
/// same chat always gives the same bytes, whether or not the CLI has folded its log in since (reading a chat does that), so a
/// copy that was only read is never taken for one that changed. Copies can't be merged, and the chat's id is written inside
/// the database, so one can't be kept as a new chat either (<see cref="Opaque"/>): a chat that changed here and on another PC
/// is set aside here, whole, and the hub's version is used.
/// </remarks>
public sealed class AntigravityFiles(string home, Func<IReadOnlyCollection<SessionSummary>> sessions) : ISessionFiles
{
    public const string Database = "conversation.db";
    public const string Annotation = "annotation.pbtxt";
    public const string Title = "title.txt";
    private const string BrainPrefix = "brain/";

    /// <summary>The CLI's data folder on this PC.</summary>
    public static string DefaultHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");

    public string Backend => AgentBackends.Antigravity;

    public bool Opaque => true;

    /// <summary>
    /// Chat ids are GUIDs in their usual lower-case form; anything else (from the settings or the hub) never becomes a path, and
    /// one chat never gets a second file under another spelling of its id.
    /// </summary>
    internal static bool IsId(string sessionId) =>
        Guid.TryParseExact(sessionId, "D", out var id) && id.ToString("D") == sessionId;

    private string DatabaseOf(string id) => Path.Combine(home, "conversations", id + ".db");

    private string BrainOf(string id) => Path.Combine(home, "brain", id);

    private string AnnotationOf(string id) => Path.Combine(home, "annotations", id + ".pbtxt");

    /// <summary>The log, when it holds anything: the CLI leaves an empty one after merely reading a chat.</summary>
    private static string? WalOf(string db) => new FileInfo(db + "-wal") is { Exists: true, Length: > 0 } ? db + "-wal" : null;

    private List<string> BrainFiles(string id) => Directory.Exists(BrainOf(id))
        ? [.. Directory.EnumerateFiles(BrainOf(id), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
        : [];

    /// <summary>The chat's files as they are on disk, the database first: what its stamp is made of.</summary>
    private List<string> FilesOf(string id)
    {
        var db = DatabaseOf(id);
        var files = new List<string> { db };
        if (WalOf(db) is { } wal)
        {
            files.Add(wal);
        }

        if (File.Exists(AnnotationOf(id)))
        {
            files.Add(AnnotationOf(id));
        }

        files.AddRange(BrainFiles(id));
        return files;
    }

    private SessionSummary? Summary(string id) => sessions().FirstOrDefault(s => s.SessionId == id);

    private string StampFor(string id, string? title) => Stamps.Of(FilesOf(id)) + (title ?? "");

    /// <summary>
    /// Titles the hub brought, with the title the list had when they came. The list (Nomad's settings) takes the new title
    /// a moment later, on the UI thread; until it changes, the hub's title stands, so the old one is never sent back as a rename.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Pulled, string? ListedBefore)> _pulledTitles = new(StringComparer.Ordinal);

    /// <summary>The chat's title: the list's, unless the list hasn't caught up with a title the hub brought.</summary>
    private string? TitleOf(string id, string? listed)
    {
        if (!_pulledTitles.TryGetValue(id, out var pulled))
        {
            return listed;
        }

        if (listed == pulled.ListedBefore)
        {
            return pulled.Pulled;
        }

        _pulledTitles.TryRemove(id, out _); // The list caught up, or the chat was renamed here since: the list's title from now on.
        return listed;
    }

    /// <summary>Whether Nomad knows the chat: listed in its settings, or brought by the hub and about to be.</summary>
    private bool Known(string id) => Summary(id) is not null || _pulledTitles.ContainsKey(id);

    public IReadOnlyList<LocalSession> List(CancellationToken ct)
    {
        var result = new List<LocalSession>();
        foreach (var s in sessions())
        {
            ct.ThrowIfCancellationRequested();
            if (!IsId(s.SessionId) || !File.Exists(DatabaseOf(s.SessionId)))
            {
                continue;
            }

            var db = DatabaseOf(s.SessionId);
            var written = File.GetLastWriteTimeUtc(db);
            if (WalOf(db) is { } wal && File.GetLastWriteTimeUtc(wal) > written)
            {
                written = File.GetLastWriteTimeUtc(wal);
            }

            // No placeholder for a missing title: it would travel as a real one.
            var title = TitleOf(s.SessionId, s.Title) ?? "";
            result.Add(new LocalSession(Backend, s.SessionId, title, s.WorkingDirectory, null, written, StampFor(s.SessionId, title)));
        }

        return result;
    }

    /// <summary>
    /// The chat's stamp; null when it isn't here, or when its files are but Nomad doesn't list it (left by a terminal, say): the
    /// hub's version then comes in as a new chat, and those files are set aside first (<see cref="WriteAsync"/>).
    /// </summary>
    public string? StampOf(string sessionId) =>
        IsId(sessionId) && File.Exists(DatabaseOf(sessionId)) && Known(sessionId)
            ? StampFor(sessionId, TitleOf(sessionId, Summary(sessionId)?.Title) ?? "")
            : null;

    public string? MainFile(string sessionId) =>
        IsId(sessionId) && File.Exists(DatabaseOf(sessionId)) ? DatabaseOf(sessionId) : null;

    /// <summary>
    /// Whether a process (the CLI) has the chat's database open: nothing may replace it then. Opening it with no sharing fails
    /// while any other handle is open, whatever sharing that one allowed.
    /// </summary>
    public bool InUse(string sessionId)
    {
        if (!IsId(sessionId) || !File.Exists(DatabaseOf(sessionId)))
        {
            return false;
        }

        try
        {
            using var _ = new FileStream(DatabaseOf(sessionId), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public async Task<IReadOnlyList<PayloadFile>> CollectAsync(LocalSession session, CancellationToken ct)
    {
        var id = session.SessionId;
        if (!IsId(id) || !File.Exists(DatabaseOf(id)))
        {
            return [];
        }

        var snapshot = new List<PayloadFile>();
        try
        {
            var copy = TempPath();
            snapshot.Add(new PayloadFile(Database, copy));
            await Task.Run(() => Snapshot(DatabaseOf(id), copy), ct).ConfigureAwait(false);
            if (File.Exists(AnnotationOf(id)))
            {
                snapshot.Add(new PayloadFile(Annotation, AnnotationOf(id)));
            }

            snapshot.AddRange(BrainFiles(id).Select(f => new PayloadFile(BrainPrefix + Path.GetRelativePath(BrainOf(id), f).Replace('\\', '/'), f)));

            // Nomad's title for it, so a rename alone is a change to upload.
            if (!string.IsNullOrWhiteSpace(session.Title))
            {
                var title = TempPath();
                snapshot.Add(new PayloadFile(Title, title));
                await File.WriteAllTextAsync(title, session.Title, ct).ConfigureAwait(false);
            }

            return snapshot;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            Release(snapshot);
            return []; // Busy or unreadable just now: the next round takes it.
        }
        catch
        {
            Release(snapshot); // Cancelled (Nomad closing), say: no copy is left behind.
            throw;
        }
    }

    /// <summary>
    /// Copies a chat's database, with its log folded in, to <paramref name="target"/> as one file, through SQLite's backup API.
    /// The chat is opened read-only, so the copy never changes it; no connection is pooled, so no file stays open.
    /// </summary>
    internal static void Snapshot(string database, string target)
    {
        SqliteSetup.Ensure();
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        source.Open();
        copy.Open();
        source.BackupDatabase(copy);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"nomad-antigravity-{Guid.NewGuid():N}");

    /// <summary>Deletes the temporary copies (the snapshot and the title); the chat's own files stay.</summary>
    public void Release(IReadOnlyList<PayloadFile> files)
    {
        foreach (var f in files.Where(f => f.Path is Database or Title))
        {
            File.Delete(f.FullPath);
        }
    }

    public Task<string?> WriteAsync(SessionDto remote, IReadOnlyDictionary<string, string> files, string folder, CancellationToken ct)
    {
        var id = remote.SessionId;
        if (!IsId(id) || !files.TryGetValue(Database, out var database))
        {
            throw new InvalidOperationException($"\"{remote.Title}\" from the hub isn't an Antigravity chat this PC can use.");
        }

        if (InUse(id))
        {
            throw new InvalidOperationException($"\"{remote.Title}\" is open in Antigravity on this PC; it is fetched once it closes.");
        }

        var db = DatabaseOf(id);
        var brain = BrainOf(id);
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);

        // Files Nomad doesn't list (left by a terminal, say) are kept before the hub's chat takes their place.
        if (File.Exists(db) && !Known(id))
        {
            SetAside(id);
        }

        // The new database is copied beside the old one first, so a failed copy changes nothing. Then the old log and index go
        // (SQLite would replay this PC's old log into the hub's database), and the database moves into place.
        var staged = db + ".nomad-hub";
        try
        {
            File.Copy(database, staged, overwrite: true);
            if (remote.LastActivityUtc > DateTime.UnixEpoch && remote.LastActivityUtc <= DateTime.UtcNow)
            {
                File.SetLastWriteTimeUtc(staged, remote.LastActivityUtc);
            }

            File.Delete(db + "-wal");
            File.Delete(db + "-shm");
            File.Move(staged, db, overwrite: true);
        }
        finally
        {
            File.Delete(staged);
        }

        if (files.TryGetValue(Annotation, out var annotation))
        {
            Stamps.Copy(annotation, AnnotationOf(id), remote.LastActivityUtc);
        }
        else if (File.Exists(AnnotationOf(id)))
        {
            File.Delete(AnnotationOf(id)); // Gone on the PC it came from.
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, downloaded) in files.Where(f => f.Key.StartsWith(BrainPrefix, StringComparison.Ordinal)))
        {
            var target = Path.GetFullPath(Path.Combine(brain, path[BrainPrefix.Length..]));
            if (target.StartsWith(Path.GetFullPath(brain) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Stamps.Copy(downloaded, target, remote.LastActivityUtc); // Never outside the chat's own folder.
                keep.Add(target);
            }
        }

        foreach (var stale in BrainFiles(id).Where(f => !keep.Contains(Path.GetFullPath(f))))
        {
            File.Delete(stale); // Gone on the PC it came from.
        }

        _pulledTitles[id] = (remote.Title, Summary(id)?.Title);
        return Task.FromResult<string?>(StampFor(id, remote.Title));
    }

    /// <summary>
    /// Keeps this PC's copy of a chat, whole, in <c>nomad-set-aside/&lt;id&gt;-&lt;time&gt;/</c> beside the CLI's folders, before
    /// the hub's version replaces it; returns that folder, or null when it couldn't (nothing is left half copied). Putting its
    /// files back where they were restores it.
    /// </summary>
    public Task<string?> SetAsideAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(IsId(sessionId) && File.Exists(DatabaseOf(sessionId)) ? SetAside(sessionId) : null);

    private string? SetAside(string id)
    {
        var target = Path.Combine(home, "nomad-set-aside", $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}");
        try
        {
            var db = DatabaseOf(id);
            var to = Path.Combine(target, Path.GetRelativePath(home, db));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Snapshot(db, to); // The log folded in: one file, which the CLI opens as it is.
            foreach (var file in new[] { AnnotationOf(id) }.Where(File.Exists).Concat(BrainFiles(id)))
            {
                to = Path.Combine(target, Path.GetRelativePath(home, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, overwrite: true);
            }

            if (Summary(id)?.Title is { Length: > 0 } title)
            {
                File.WriteAllText(Path.Combine(target, Title), title); // Nomad's title for it, which the CLI's files don't hold.
            }

            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }
    }
}

/// <summary>SQLite's native library, set up once and explicitly (NativeAOT trims the lookup Microsoft.Data.Sqlite does by itself).</summary>
internal static class SqliteSetup
{
    private static readonly Lazy<bool> Ready = new(() =>
    {
        SQLitePCL.Batteries_V2.Init();
        return true;
    });

    public static void Ensure() => _ = Ready.Value;
}

/// <summary>
/// OpenCode keeps sessions in its database, so they travel as <c>opencode export</c> JSON and come back with
/// <c>opencode import</c>, run in the session's folder here (the import files it under the folder it runs in).
/// </summary>
public sealed class OpenCodeFiles(Func<string?> cliPath) : ISessionFiles
{
    public const string Export = "export.json";

    public string Backend => AgentBackends.OpenCode;

    private List<LocalSession>? _last;

    private async Task<string> RunAsync(string arguments, string? workingDirectory, CancellationToken ct)
    {
        var exe = cliPath();
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new FileNotFoundException("OpenCode isn't installed.");
        }

        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("OpenCode didn't start.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        _ = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0 ? await stdout.ConfigureAwait(false) : throw new InvalidOperationException($"opencode {arguments.Split(' ')[0]} failed ({process.ExitCode}).");
    }

    public IReadOnlyList<LocalSession> List(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cliPath()))
        {
            return [];
        }

        try
        {
            // The server's list has every project's sessions; `opencode session list` only the folder's it runs in.
            var server = OpenCode.OpenCodeServer.GetAsync(ct).GetAwaiter().GetResult();
            var json = server.Http.GetStringAsync("experimental/session?limit=5000", ct).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var result = new List<LocalSession>();
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (OpenCode.OpenCodeJson.Str(s, "id") is not { } id)
                {
                    continue;
                }

                var updated = OpenCode.OpenCodeJson.Long(OpenCode.OpenCodeJson.Obj(s, "time"), "updated") ?? 0;
                result.Add(new LocalSession(Backend, id, OpenCode.OpenCodeJson.Str(s, "title") ?? "", OpenCode.OpenCodeJson.Str(s, "directory"), null,
                    DateTimeOffset.FromUnixTimeMilliseconds(updated).UtcDateTime, updated.ToString()));
            }

            _last = result;
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or HttpRequestException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return [];
        }
    }

    public string? StampOf(string sessionId) => _last?.FirstOrDefault(s => s.SessionId == sessionId)?.Stamp;

    public string? MainFile(string sessionId) => null;

    public async Task<IReadOnlyList<PayloadFile>> CollectAsync(LocalSession session, CancellationToken ct)
    {
        var json = await RunAsync($"export {session.SessionId}", null, ct).ConfigureAwait(false);
        var start = json.IndexOf('{');
        if (start < 0)
        {
            return [];
        }

        var temp = Path.Combine(Path.GetTempPath(), $"nomad-opencode-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(temp, json[start..], ct).ConfigureAwait(false);
        return [new PayloadFile(Export, temp)];
    }

    public void Release(IReadOnlyList<PayloadFile> files)
    {
        foreach (var f in files)
        {
            File.Delete(f.FullPath);
        }
    }

    public async Task<string?> WriteAsync(SessionDto remote, IReadOnlyDictionary<string, string> files, string folder, CancellationToken ct)
    {
        if (files.TryGetValue(Export, out var export))
        {
            await RunAsync($"import \"{export}\"", Directory.Exists(folder) ? folder : null, ct).ConfigureAwait(false);
        }

        List(ct);
        return StampOf(remote.SessionId);
    }
}
