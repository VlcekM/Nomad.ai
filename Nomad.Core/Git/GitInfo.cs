using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Nomad.Core.Git;

/// <summary>One commit: short hash, subject line, author and time.</summary>
public sealed record GitCommit(string Hash, string Subject, string Author, DateTimeOffset When);

/// <summary>
/// A folder's git state, for the new-session project cards and the side panel: the branch, how far it is
/// from its upstream, how many files are changed and the latest commits.
/// </summary>
public sealed record GitStatus(string Branch, int Ahead, int Behind, int Changes, IReadOnlyList<GitCommit> Commits);

/// <summary>
/// Reads <see cref="GitStatus"/> with the git CLI (read-only, <c>--no-optional-locks</c>, so it never takes the
/// index lock from a git command the user runs). Results are cached briefly per folder. Best effort: no git, not a
/// repository or a slow one gives null.
/// </summary>
public static class GitInfo
{
    // A text ending in a line break has no line after it.
    private static IEnumerable<string> SkipLastIf(this IEnumerable<string> lines, bool skip) => skip ? lines.SkipLast(1) : lines;

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    private static readonly Dictionary<string, (DateTime At, Task<GitStatus?> Task)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<GitStatus?> GetAsync(string folder, int commits = 5, bool refresh = false)
    {
        var key = folder.TrimEnd('\\', '/') + "|" + commits;
        lock (Cache)
        {
            if (!refresh && Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < Fresh)
            {
                return hit.Task;
            }

            var task = Task.Run(() => ReadAsync(folder, commits));
            Cache[key] = (DateTime.UtcNow, task);
            return task;
        }
    }

    private static async Task<GitStatus?> ReadAsync(string folder, int commits)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var status = RunAsync(folder, "status", "--porcelain=v1", "--branch", "--untracked-files=normal");
        var log = RunAsync(folder, "log", $"-n{commits}", "--format=%h%x1f%s%x1f%an%x1f%ct");
        var statusText = await status;
        if (statusText is null)
        {
            return null; // Not a repository (or no git).
        }

        var (branch, ahead, behind, changes) = ParseStatus(statusText);
        return new GitStatus(branch, ahead, behind, changes, ParseLog(await log ?? ""));
    }

    /// <summary>"## main...origin/main [ahead 1, behind 2]" and one line per changed file.</summary>
    public static (string Branch, int Ahead, int Behind, int Changes) ParseStatus(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string branch = "";
        int ahead = 0, behind = 0;
        if (lines.FirstOrDefault() is { } head && head.StartsWith("## ", StringComparison.Ordinal))
        {
            var rest = head[3..];
            var bracket = rest.IndexOf(" [", StringComparison.Ordinal);
            var counts = bracket >= 0 ? rest[(bracket + 2)..].TrimEnd(']') : "";
            rest = bracket >= 0 ? rest[..bracket] : rest;
            branch = rest.StartsWith("No commits yet on ", StringComparison.Ordinal) ? rest[18..]
                : rest.Split("...")[0];
            if (branch.StartsWith("HEAD (no branch)", StringComparison.Ordinal))
            {
                branch = "detached";
            }

            foreach (var part in counts.Split(',', StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("ahead ", StringComparison.Ordinal))
                {
                    int.TryParse(part[6..], CultureInfo.InvariantCulture, out ahead);
                }
                else if (part.StartsWith("behind ", StringComparison.Ordinal))
                {
                    int.TryParse(part[7..], CultureInfo.InvariantCulture, out behind);
                }
            }
        }

        return (branch, ahead, behind, lines.Count(l => !l.StartsWith("## ", StringComparison.Ordinal)));
    }

    public static IReadOnlyList<GitCommit> ParseLog(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\x1f'))
            .Where(f => f.Length == 4 && long.TryParse(f[3], CultureInfo.InvariantCulture, out _))
            .Select(f => new GitCommit(f[0], f[1], f[2], DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[3], CultureInfo.InvariantCulture))))
            .ToList();

    /// <summary>Where worktrees go inside a repository, as the CLI's <c>--worktree</c> puts them.</summary>
    private static readonly string WorktreesPart = Path.Combine(".claude", "worktrees");

    /// <summary>
    /// Makes a new worktree of <paramref name="folder"/>'s repository at its HEAD, in <c>.claude\worktrees\&lt;name&gt;</c>
    /// on a new branch <c>worktree-&lt;name&gt;</c> (as the CLI's <c>--worktree</c> does), and returns the folder to work in:
    /// the same subfolder of the new worktree when <paramref name="folder"/> is below the repository's root.
    /// </summary>
    public static async Task<string> AddWorktreeAsync(string folder, string? from = null)
    {
        var (rootOk, rootText, _) = await RunFullAsync(folder, Timeout, "rev-parse", "--show-toplevel");
        if (!rootOk || string.IsNullOrWhiteSpace(rootText))
        {
            throw new InvalidOperationException("Can't start in a worktree: the folder is not a git repository.");
        }

        var root = Path.GetFullPath(rootText.Trim());
        var name = DateTime.Now.ToString("yyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(root, WorktreesPart, name);
        var (ok, _, error) = await RunFullAsync(root, TimeSpan.FromSeconds(60), "worktree", "add", "-b", "worktree-" + name, path, string.IsNullOrEmpty(from) ? "HEAD" : from);
        if (!ok)
        {
            throw new InvalidOperationException("Can't make a worktree: " + (string.IsNullOrWhiteSpace(error) ? "git failed." : error.Trim()));
        }

        var relative = Path.GetRelativePath(root, Path.GetFullPath(folder));
        return relative is "." || relative.StartsWith("..", StringComparison.Ordinal) ? path : Path.Combine(path, relative);
    }

    /// <summary>A file's lines as last committed (HEAD); null when it isn't in a repository or isn't committed.</summary>
    public static async Task<string[]?> HeadLinesAsync(string file)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is not { } folder)
        {
            return null;
        }

        var (ok, text, _) = await RunFullAsync(folder, Timeout, "show", "HEAD:./" + Path.GetFileName(file));
        return ok ? text.Split('\n').Select(l => l.TrimEnd('\r')).SkipLastIf(text.EndsWith('\n')).ToArray() : null;
    }

    /// <summary>The local branches, the most recently committed to first; empty when it is no repository.</summary>
    public static async Task<IReadOnlyList<string>> BranchesAsync(string folder)
    {
        var text = await RunAsync(folder, "for-each-ref", "--sort=-committerdate", "--format=%(refname:short)", "refs/heads");
        return (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>How many commits a local branch has that its upstream does not (not pushed yet); 0 without an upstream.</summary>
    public static async Task<int> AheadAsync(string folder, string branch)
    {
        var text = await RunAsync(folder, "rev-list", "--count", $"{branch}@{{upstream}}..{branch}", "--");
        return int.TryParse(text?.Trim(), CultureInfo.InvariantCulture, out var ahead) ? ahead : 0;
    }

    /// <summary>Checks out another branch in the folder (<c>git switch</c>); null when done, or git's reason it could not.</summary>
    public static async Task<string?> SwitchBranchAsync(string folder, string branch)
    {
        var (ok, _, error) = await RunFullAsync(folder, TimeSpan.FromSeconds(30), "switch", branch);
        if (ok)
        {
            lock (Cache)
            {
                Cache.Clear();
            }

            return null;
        }

        return string.IsNullOrWhiteSpace(error) ? "git switch failed." : error.Trim();
    }

    /// <summary>The repository a worktree folder (<c>…\.claude\worktrees\&lt;name&gt;</c>, or below it) belongs to; null for any other folder.</summary>
    public static string? WorktreeOwner(string folder)
    {
        var marker = Path.DirectorySeparatorChar + WorktreesPart + Path.DirectorySeparatorChar;
        var at = folder.Replace('/', Path.DirectorySeparatorChar).IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at > 0 ? folder[..at] : null;
    }

    private static readonly Dictionary<string, (DateTime At, Task<Func<string, bool>?> Task)> MergeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which branches of a repository were merged into what it has checked out: a test for a branch name. A branch
    /// counts once a merge commit took in its tip (so a new worktree, still at the commit it started from, does not,
    /// nor one that got more commits since); a deleted one by the merge's "Merge branch 'name'" subject. Null when it
    /// is no repository.
    /// </summary>
    public static Task<Func<string, bool>?> MergedBranchesAsync(string repo)
    {
        var key = repo.TrimEnd('\\', '/');
        lock (MergeCache)
        {
            if (MergeCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < Fresh)
            {
                return hit.Task;
            }

            var task = Task.Run(() => ReadMergedAsync(key));
            MergeCache[key] = (DateTime.UtcNow, task);
            return task;
        }
    }

    private static async Task<Func<string, bool>?> ReadMergedAsync(string repo)
    {
        if (!Directory.Exists(repo))
        {
            return null;
        }

        var merges = RunAsync(repo, "log", "--merges", "-n500", "--format=%P%x1f%s", "HEAD");
        var refs = RunAsync(repo, "for-each-ref", "--format=%(refname:short)%x1f%(objectname)", "refs/heads");
        if (await merges is not { } mergeText || await refs is not { } refText)
        {
            return null;
        }

        var takenIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in mergeText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\x1f');
            foreach (var parent in fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                takenIn.Add(parent);
            }

            if (fields.Length > 1 && fields[1].StartsWith("Merge branch '", StringComparison.Ordinal)
                && fields[1].IndexOf('\'', 14) is var end and > 14)
            {
                named.Add(fields[1][14..end]);
            }
        }

        var tips = refText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\x1f'))
            .Where(f => f.Length == 2)
            .ToDictionary(f => f[0], f => f[1], StringComparer.Ordinal);
        return branch => tips.TryGetValue(branch, out var tip) ? takenIn.Contains(tip) : named.Contains(branch);
    }

    /// <summary>Runs <c>git -C folder …</c>; its output, or null when it fails or takes too long.</summary>
    internal static async Task<string?> RunAsync(string folder, params string[] args)
    {
        var (ok, output, _) = await RunFullAsync(folder, Timeout, args);
        return ok ? output : null;
    }

    /// <summary>Runs <c>git -C folder …</c>: whether it succeeded in time, and its output and error text.</summary>
    private static Task<(bool Ok, string Output, string Error)> RunFullAsync(string folder, TimeSpan timeout, params string[] args) =>
        RunToolAsync("git", ["--no-optional-locks", "-c", "core.quotepath=off", "-C", folder, .. args], null, timeout);

    /// <summary>Runs a command-line tool (git, gh) without a window: whether it succeeded in time, its output and error.</summary>
    internal static async Task<(bool Ok, string Output, string Error)> RunToolAsync(
        string tool, IEnumerable<string> args, string? workingDirectory, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return (false, "", "");
            }

            using var cts = new CancellationTokenSource(timeout);
            var output = process.StandardOutput.ReadToEndAsync(cts.Token);
            var error = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (false, "", $"{tool} took too long.");
            }

            return (process.ExitCode == 0, await output, await error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            return (false, "", ex.Message);
        }
    }
}
