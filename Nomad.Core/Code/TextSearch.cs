using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Nomad.Core.Code;

/// <summary>One match of a find in files: where (line and column 0-based), how long, and its line's text.</summary>
public sealed record TextHit(string Path, int Line, int Column, int Length, string Text);

/// <summary>
/// Find in files (Ctrl+Shift+F): every match of a text or a regular expression in a folder's files, the ones the code
/// search indexes (git's, or a walk that leaves out build output), skipping binary and very large files.
/// </summary>
public static class TextSearch
{
    private const long MaxFileSize = 4 * 1024 * 1024;

    /// <summary>The pattern for a query; throws <see cref="ArgumentException"/> for an expression that isn't valid.</summary>
    public static Regex Pattern(string query, bool matchCase, bool wholeWord, bool regex)
    {
        var pattern = regex ? query : Regex.Escape(query);
        if (wholeWord)
        {
            pattern = $@"(?<![\w]){pattern}(?![\w])";
        }

        return new Regex(pattern, RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(250));
    }

    /// <summary>The matches under a folder, in file order and then line order, up to <paramref name="max"/> of them.</summary>
    public static async Task<List<TextHit>> SearchAsync(string root, Regex pattern, int max, CancellationToken cancel = default)
    {
        var files = await CodeIndex.ListFilesAsync(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var found = new ConcurrentDictionary<string, List<TextHit>>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Environment.ProcessorCount }, async (file, token) =>
        {
            if (Volatile.Read(ref total) >= max || await ReadAsync(file, token) is not { } text)
            {
                return;
            }

            var hits = new List<TextHit>();
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length && hits.Count < max; i++)
            {
                var line = lines[i].TrimEnd('\r');
                try
                {
                    for (var m = pattern.Match(line); m.Success && hits.Count < max; m = m.NextMatch())
                    {
                        if (m.Length > 0)
                        {
                            hits.Add(new TextHit(file, i, m.Index, m.Length, line));
                        }
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological line: on to the next.
                }
            }

            if (hits.Count > 0)
            {
                found[file] = hits;
                Interlocked.Add(ref total, hits.Count);
            }
        });

        return found.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).SelectMany(f => f.Value).Take(max).ToList();
    }

    // The file's text, or null for one that is too big, binary (a NUL early on) or can't be read.
    private static async Task<string?> ReadAsync(string file, CancellationToken cancel)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxFileSize)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(file, cancel);
            return text.AsSpan(0, Math.Min(text.Length, 8000)).Contains('\0') ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
