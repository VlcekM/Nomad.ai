namespace Nomad.Core.Git;

/// <summary>A file's state in git, as the file tree marks it.</summary>
public enum GitFileState
{
    /// <summary>Not in a repository, or ignored: no mark.</summary>
    None,

    /// <summary>Tracked and unchanged (Visual Studio's lock).</summary>
    Clean,

    /// <summary>Changed since the last commit (its red check).</summary>
    Modified,

    /// <summary>New: untracked, or added to the index (its green plus).</summary>
    Added,

    /// <summary>Deleted (counted on its folders; the file is gone from the tree).</summary>
    Removed,
}

/// <summary>
/// Every file's git state under a repository, from one <c>git status</c>: the changed files, and the ignored ones
/// (anything else in the repository is tracked and clean), with each folder's count of changed files below it.
/// </summary>
public sealed class GitFiles
{
    private readonly string _root;
    private readonly Dictionary<string, GitFileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ignored = [];
    private readonly Dictionary<string, (int Modified, int Added, int Removed)> _folders = new(StringComparer.OrdinalIgnoreCase);

    private GitFiles(string root) => _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>The state of the files under a folder; null when it is no repository (or git is missing or slow).</summary>
    public static async Task<GitFiles?> ReadAsync(string folder)
    {
        var top = await GitInfo.RunAsync(folder, "rev-parse", "--show-toplevel");
        if (string.IsNullOrWhiteSpace(top))
        {
            return null;
        }

        var status = await GitInfo.RunAsync(folder, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching");
        return status is null ? null : Parse(top.Trim(), status);
    }

    /// <summary>
    /// <c>git status --porcelain=v1 -z</c>: "XY path" entries ended by NUL, a rename's or copy's old path after it;
    /// "??" untracked, "!!" ignored (a folder ends in "/").
    /// </summary>
    public static GitFiles Parse(string root, string status)
    {
        var files = new GitFiles(root);
        var entries = status.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4)
            {
                continue;
            }

            var (x, y, path) = (entry[0], entry[1], entry[3..]);
            if (x is 'R' or 'C')
            {
                i++; // The old path.
            }

            if (x == '!')
            {
                files._ignored.Add(files.Full(path.TrimEnd('/')));
                continue;
            }

            var state = (x, y) switch
            {
                ('?', '?') or ('A', _) => GitFileState.Added,
                (_, 'D') or ('D', _) => GitFileState.Removed,
                _ => GitFileState.Modified,
            };
            var full = files.Full(path);
            files._files[full] = state;
            files.Count(full, state);
        }

        return files;
    }

    /// <summary>A file's state (a file outside the repository has none).</summary>
    public GitFileState StateOf(string path)
    {
        if (!IsUnder(path, _root))
        {
            return GitFileState.None;
        }

        if (_files.TryGetValue(path, out var state))
        {
            return state;
        }

        return _ignored.Exists(ignored => IsUnder(path, ignored)) ? GitFileState.None : GitFileState.Clean;
    }

    /// <summary>How many files below a folder (at any depth) are modified, added and removed.</summary>
    public (int Modified, int Added, int Removed) CountsUnder(string folder) =>
        _folders.GetValueOrDefault(Path.TrimEndingDirectorySeparator(folder));

    private string Full(string relative) => Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private void Count(string file, GitFileState state)
    {
        for (var folder = Path.GetDirectoryName(file); folder is not null && IsUnder(folder, _root); folder = Path.GetDirectoryName(folder))
        {
            var (m, a, r) = _folders.GetValueOrDefault(folder);
            _folders[folder] = state switch
            {
                GitFileState.Added => (m, a + 1, r),
                GitFileState.Removed => (m, a, r + 1),
                _ => (m + 1, a, r),
            };
        }
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
        (path.Length == folder.Length || path[folder.Length] == Path.DirectorySeparatorChar);
}
