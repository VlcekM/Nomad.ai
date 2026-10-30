namespace Nomad.Core.Hub;

/// <summary>
/// The path-independent name of a project (docs/hub.md §3): its git <c>origin</c> normalised
/// (<c>github.com/vlcekm/nomad.ai</c>), else <c>folder:&lt;name&gt;</c>.
/// </summary>
public static class ProjectKeys
{
    public static (string Key, string Name)? For(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var root = RepositoryRoot(folder);
        var name = Path.GetFileName((root ?? folder).TrimEnd('\\', '/'));
        if (root is not null && OriginOf(root) is { } origin && Normalise(origin) is { Length: > 0 } key)
        {
            return (key, name);
        }

        return string.IsNullOrEmpty(name) ? null : ("folder:" + name.ToLowerInvariant(), name);
    }

    private static string? RepositoryRoot(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>The <c>config</c> of a repository, following a worktree's <c>.git</c> file to the main repository.</summary>
    private static string? ConfigOf(string root)
    {
        var git = Path.Combine(root, ".git");
        if (Directory.Exists(git))
        {
            return Path.Combine(git, "config");
        }

        try
        {
            var line = File.ReadLines(git).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line is null)
            {
                return null;
            }

            var gitDir = Path.GetFullPath(Path.Combine(root, line["gitdir:".Length..].Trim()));
            var common = Path.Combine(gitDir, "commondir");
            if (File.Exists(common))
            {
                gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(common).Trim()));
            }

            return Path.Combine(gitDir, "config");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? OriginOf(string root)
    {
        if (ConfigOf(root) is not { } config || !File.Exists(config))
        {
            return null;
        }

        try
        {
            var inOrigin = false;
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inOrigin = line.Replace(" ", "") == "[remote\"origin\"]";
                }
                else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal) && line.IndexOf('=') is var eq and > 0)
                {
                    return line[(eq + 1)..].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary><c>git@github.com:Me/Repo.git</c>, <c>https://me@github.com/Me/Repo</c> → <c>github.com/me/repo</c>.</summary>
    internal static string Normalise(string url)
    {
        var u = url.Trim();
        var scheme = u.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            u = u[(scheme + 3)..];
        }
        else if (u.IndexOf(':') is var colon and > 1 && !u.Contains('\\'))
        {
            u = u[..colon] + "/" + u[(colon + 1)..]; // scp form: host:path
        }

        if (u.IndexOf('@') is var at and >= 0 && at < (u.IndexOf('/') is var slash and >= 0 ? slash : u.Length))
        {
            u = u[(at + 1)..];
        }

        u = u.Replace('\\', '/').TrimEnd('/');
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            u = u[..^4];
        }

        return u.ToLowerInvariant();
    }
}
