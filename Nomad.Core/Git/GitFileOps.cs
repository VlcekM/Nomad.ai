namespace Nomad.Core.Git;

/// <summary>
/// What the file tree's menu does with git for a file or folder: stage, unstage, undo the changes, commit. Each
/// returns null when done, or git's reason it could not.
/// </summary>
public static class GitFileOps
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Stages a file or folder's changes, new and deleted files too (<c>git add -A</c>).</summary>
    public static Task<string?> StageAsync(string path) => RunAsync(path, "add", "-A", "--");

    /// <summary>Takes a file or folder's changes out of the index again, leaving them in the files.</summary>
    public static Task<string?> UnstageAsync(string path) => RunAsync(path, "restore", "--staged", "--");

    /// <summary>
    /// Puts a file or folder back as last committed, staged changes too; files git doesn't know of yet stay.
    /// </summary>
    public static Task<string?> RevertAsync(string path) => RunAsync(path, "restore", "--source=HEAD", "--staged", "--worktree", "--");

    /// <summary>Commits a file or folder's changes (staging them first), and only those, with a message.</summary>
    public static async Task<string?> CommitAsync(string path, string message)
    {
        if (await StageAsync(path) is { } error)
        {
            return error;
        }

        return await RunAsync(path, "commit", "-m", message, "--");
    }

    // Runs in the path's folder (a file's own one) with the path last, relative to it.
    private static async Task<string?> RunAsync(string path, params string[] args)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        if (folder is null)
        {
            return "Not in a repository.";
        }

        var target = Directory.Exists(full) ? "." : Path.GetFileName(full);
        var (ok, _, error) = await GitInfo.RunFullAsync(folder, Timeout, [.. args, target]);
        return ok ? null : string.IsNullOrWhiteSpace(error) ? "git failed." : error.Trim();
    }
}
