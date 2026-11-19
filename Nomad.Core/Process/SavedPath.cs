namespace Nomad.Core.Process;

/// <summary>
/// Brings the process PATH up to date with the PATH saved in Windows. A program started from an Explorer that predates an
/// install (Git, say) inherits the old PATH, and so does everything it starts: the agent CLIs and their shells would not find git.
/// </summary>
public static class SavedPath
{
    /// <summary>
    /// Appends the saved machine and user PATH folders the process PATH lacks, in that order (as Windows builds a new PATH),
    /// keeping the inherited folders first. Does nothing off Windows.
    /// </summary>
    public static void Refresh()
    {
        if (!OperatingSystem.IsWindows()) return;

        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var known = new HashSet<string>(Split(current).Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var missing = SavedFolders().Where(dir => known.Add(Normalize(dir))).ToList();
        if (missing.Count == 0) return;

        var joined = string.Join(Path.PathSeparator, missing);
        var path = current.Length == 0 ? joined : current.TrimEnd(Path.PathSeparator) + Path.PathSeparator + joined;
        Environment.SetEnvironmentVariable("PATH", path);
    }

    /// <summary>
    /// The saved machine PATH's folders, then the user PATH's, with environment variables expanded.
    /// </summary>
    private static IEnumerable<string> SavedFolders() =>
        Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine))
            .Concat(Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)))
            .Select(Environment.ExpandEnvironmentVariables);

    /// <summary>
    /// A PATH value's folders, without empty entries.
    /// </summary>
    private static IEnumerable<string> Split(string? path) =>
        (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A folder as compared for duplicates: unquoted, without a trailing separator.
    /// </summary>
    private static string Normalize(string dir) =>
        dir.Trim('"').TrimEnd('\\', '/');
}
