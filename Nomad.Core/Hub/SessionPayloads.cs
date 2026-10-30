using System.Diagnostics;
using System.Text;
using System.Text.Json;
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

    public static void Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        var temp = to + ".nomad-hub";
        File.Copy(from, temp, overwrite: true);
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

            Stamps.Copy(downloaded, target);
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
                Stamps.Copy(downloaded, target);
            }
        }

        return Task.FromResult(StampOf(remote.SessionId));
    }
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
