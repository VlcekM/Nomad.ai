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

    /// <summary>Runs <c>git -C folder …</c>; its output, or null when it fails or takes too long.</summary>
    private static async Task<string?> RunAsync(string folder, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "--no-optional-locks", "-c", "core.quotepath=off", "-C", folder }.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return null;
            }

            using var cts = new CancellationTokenSource(Timeout);
            var output = process.StandardOutput.ReadToEndAsync(cts.Token);
            _ = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? await output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            return null;
        }
    }
}
