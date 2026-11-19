using System.Collections.Concurrent;

namespace Nomad.Core.Process;

/// <summary>
/// Finds command-line tools (<c>claude</c>, <c>git</c>, <c>gh</c>, …) the way a shell would, and also where a shell started earlier
/// would not: a tool whose folder was added to PATH after Explorer started is missing from the PATH Nomad inherited.
/// </summary>
public static class CliLocator
{
    /// <summary>
    /// The extensions tried for a bare name on Windows, in order.
    /// </summary>
    private static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat"];

    /// <summary>
    /// Tools already found by <see cref="ResolveTool"/>, by name. Misses are not cached, so a tool installed later is found then.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a configured CLI path or bare command name to a full file path, or null when nothing matches. Bare names are
    /// searched on the process PATH (with PATHEXT-style extensions on Windows), then on the saved machine and user PATH, then in
    /// the default install folders (<c>%USERPROFILE%\.local\bin</c>, Git for Windows, GitHub CLI).
    /// </summary>
    public static string? Resolve(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "claude" : configured.Trim().Trim('"');
        var extensions = OperatingSystem.IsWindows() ? WindowsExtensions : [];

        if (Path.IsPathRooted(name) || name.Contains('\\') || name.Contains('/'))
            return ProbeFile(Path.GetFullPath(name), extensions);

        return SearchDirectories()
            .Select(dir => ProbeDirectory(dir, name, extensions))
            .FirstOrDefault(found => found is not null);
    }

    /// <summary>
    /// The full path of an executable tool for <see cref="System.Diagnostics.ProcessStartInfo"/>, or the bare name when it is not
    /// found (or is only a batch file, which needs <c>cmd.exe</c>), so starting it fails as it did before.
    /// </summary>
    public static string ResolveTool(string name)
    {
        if (Tools.TryGetValue(name, out var cached)) return cached;

        var found = Resolve(name);
        if (found is null || IsBatchFile(found)) return name;

        Tools[name] = found;
        return found;
    }

    /// <summary>True when the file must be started through <c>cmd.exe</c> (npm shims).</summary>
    public static bool IsBatchFile(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The folders to search, in order: the process PATH, the saved machine and user PATH (Windows), then default install folders.
    /// </summary>
    private static IEnumerable<string> SearchDirectories()
    {
        foreach (var dir in SplitPath(Environment.GetEnvironmentVariable("PATH")))
            yield return dir;

        if (OperatingSystem.IsWindows())
        {
            foreach (var dir in SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)))
                yield return dir;

            foreach (var dir in SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)))
                yield return dir;
        }

        foreach (var dir in DefaultInstallDirectories())
            yield return dir;
    }

    /// <summary>
    /// The folders installers put tools in by default, for when no PATH has them.
    /// </summary>
    private static IEnumerable<string> DefaultInstallDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, ".local", "bin");

        if (!OperatingSystem.IsWindows()) yield break;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        yield return Path.Combine(programFiles, "Git", "cmd");
        yield return Path.Combine(localPrograms, "Git", "cmd");
        yield return Path.Combine(programFiles, "GitHub CLI");
    }

    /// <summary>
    /// A PATH value's folders, with quotes trimmed and environment variables expanded (saved values may hold <c>%SystemRoot%</c>).
    /// </summary>
    private static IEnumerable<string> SplitPath(string? path) =>
        (path ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Environment.ExpandEnvironmentVariables(dir.Trim('"')));

    /// <summary>
    /// The tool's file in one folder, or null when it is not there or the folder is not a valid path.
    /// </summary>
    private static string? ProbeDirectory(string dir, string name, string[] extensions)
    {
        try
        {
            return ProbeFile(Path.Combine(dir, name), extensions);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The candidate itself when it has an extension and exists, else the first of it plus one of the extensions that exists.
    /// </summary>
    private static string? ProbeFile(string candidate, string[] extensions)
    {
        if (Path.HasExtension(candidate) && File.Exists(candidate)) return candidate;

        foreach (var ext in extensions)
        {
            if (File.Exists(candidate + ext)) return candidate + ext;
        }

        return !OperatingSystem.IsWindows() && File.Exists(candidate) ? candidate : null;
    }
}
