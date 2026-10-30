namespace Nomad.Core.Files;

/// <summary>One entry of a folder, as the file tree lists it.</summary>
public readonly record struct DirectoryEntry(string Name, string FullPath, bool IsDirectory);

/// <summary>Lists a folder for the file tree: folders first, then files, each by name (case-insensitive).</summary>
public static class DirectoryListing
{
    /// <summary>Never listed: git's own folder.</summary>
    public static bool IsHidden(string name) => name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Left out of the tree unless it shows all files, as Visual Studio's Solution Explorer leaves them out of an SDK
    /// project (its default item excludes): build output (<c>bin</c>, <c>obj</c>), dot folders (<c>.vs</c>,
    /// <c>.idea</c>), <c>node_modules</c> and per-user files (<c>*.user</c>, <c>*.suo</c>).
    /// </summary>
    public static bool IsExcluded(string name, bool isDirectory) => isDirectory
        ? name.StartsWith('.') || name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
          name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        : name.EndsWith(".user", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".suo", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder's entries; empty when it is gone or can't be read.</summary>
    public static List<DirectoryEntry> List(string folder)
    {
        var entries = new List<DirectoryEntry>();
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false };
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                if (!IsHidden(info.Name))
                {
                    entries.Add(new DirectoryEntry(info.Name, info.FullName, info is DirectoryInfo));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        entries.Sort(Compare);
        return entries;
    }

    public static int Compare(DirectoryEntry a, DirectoryEntry b) =>
        a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : NameComparer.Compare(a.Name, b.Name);

    /// <summary>By name, ignoring case, numbers by value ("file2" before "file10"), as Explorer sorts.</summary>
    public static readonly IComparer<string> NameComparer = Comparer<string>.Create(CompareNames);

    private static int CompareNames(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && a[i] == '0') i++;
                while (j < b.Length && b[j] == '0') j++;
                var ni = i;
                var nj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var byLength = (i - ni).CompareTo(j - nj);
                if (byLength != 0)
                {
                    return byLength;
                }

                var byDigits = string.CompareOrdinal(a, ni, b, nj, i - ni);
                if (byDigits != 0)
                {
                    return byDigits;
                }

                // Equal values: fewer leading zeros first.
                var byZeros = (ni - si).CompareTo(nj - sj);
                if (byZeros != 0)
                {
                    return byZeros;
                }

                continue;
            }

            var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (c != 0)
            {
                return c;
            }

            i++;
            j++;
        }

        var byRest = (a.Length - i).CompareTo(b.Length - j);
        return byRest != 0 ? byRest : string.CompareOrdinal(a, b);
    }
}
