namespace Nomad.Core.Process;

/// <summary>Finds the <c>claude</c> executable the way a shell would.</summary>
public static class CliLocator
{
    private static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat"];

    /// <summary>
    /// Resolves a configured CLI path or bare command name to a full file path, or null when nothing
    /// matches. Bare names are searched on PATH (with PATHEXT-style extensions on Windows) and then in
    /// the native installer's default folder <c>%USERPROFILE%\.local\bin</c>.
    /// </summary>
    public static string? Resolve(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "claude" : configured.Trim().Trim('"');
        var extensions = OperatingSystem.IsWindows() ? WindowsExtensions : [];

        if (Path.IsPathRooted(name) || name.Contains('\\') || name.Contains('/'))
        {
            return ProbeFile(Path.GetFullPath(name), extensions);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim('"'), name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var found = ProbeFile(candidate, extensions);
            if (found is not null)
            {
                return found;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var found = ProbeFile(Path.Combine(home, ".local", "bin", name), extensions);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? ProbeFile(string candidate, string[] extensions)
    {
        if (Path.HasExtension(candidate) && File.Exists(candidate))
        {
            return candidate;
        }

        foreach (var ext in extensions)
        {
            if (File.Exists(candidate + ext))
            {
                return candidate + ext;
            }
        }

        return !OperatingSystem.IsWindows() && File.Exists(candidate) ? candidate : null;
    }

    /// <summary>True when the file must be started through <c>cmd.exe</c> (npm shims).</summary>
    public static bool IsBatchFile(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}
