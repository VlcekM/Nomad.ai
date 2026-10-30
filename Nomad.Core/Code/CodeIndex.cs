using System.Collections.Concurrent;
using Nomad.Core.Files;
using Nomad.Core.Git;

namespace Nomad.Core.Code;

/// <summary>One result of a code search: the symbol (or file), how well it matched, and which characters did.</summary>
public sealed record CodeMatch(CodeSymbol Symbol, int Score, int[] Positions);

/// <summary>
/// A folder's files and the symbols declared in them, for Ctrl+T in the Files view. The files are git's (tracked and
/// untracked, not ignored) in a repository, else the folder's minus build output and dot folders. Each refresh
/// re-reads only the files that changed since the last; it is safe to search while one runs.
/// </summary>
public sealed class CodeIndex(string root)
{
    private const int MaxFiles = 50_000;
    private const long MaxFileSize = 1024 * 1024;
    private const int MaxResults = 150;

    private readonly ConcurrentDictionary<string, (DateTime Written, long Length, CodeSymbol[] Symbols)> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private volatile Snapshot _snapshot = new([], []);

    private sealed record Snapshot(CodeSymbol[] Symbols, CodeSymbol[] Files);

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>Indexed at least once.</summary>
    public bool IsReady { get; private set; }

    public int SymbolCount => _snapshot.Symbols.Length;

    /// <summary>Lists the files again and re-reads those that changed.</summary>
    public async Task RefreshAsync(CancellationToken cancel = default)
    {
        await _refreshing.WaitAsync(cancel);
        try
        {
            var files = await ListFilesAsync(Root);
            await Task.Run(() =>
            {
                var seen = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                foreach (var gone in _scanned.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    _scanned.TryRemove(gone, out _);
                }

                Parallel.ForEach(files.Where(SymbolScanner.CanScan), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, Scan);
                var symbols = _scanned.Values.SelectMany(v => v.Symbols).ToArray();
                var fileSymbols = files.Select(f => new CodeSymbol(Path.GetFileName(f), SymbolKind.File, null, Relative(Path.GetDirectoryName(f) ?? Root), f, 0, 0)).ToArray();
                _snapshot = new Snapshot(symbols, fileSymbols);
            }, cancel);
            IsReady = true;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private void Scan(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxFileSize)
            {
                _scanned.TryRemove(file, out _);
                return;
            }

            if (_scanned.TryGetValue(file, out var known) && known.Written == info.LastWriteTimeUtc && known.Length == info.Length)
            {
                return;
            }

            var symbols = SymbolScanner.Scan(file, File.ReadAllText(file)).ToArray();
            _scanned[file] = (info.LastWriteTimeUtc, info.Length, symbols);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _scanned.TryRemove(file, out _);
        }
    }

    private string Relative(string folder)
    {
        var relative = Path.GetRelativePath(Root, folder);
        return relative == "." ? "" : relative.Replace('\\', '/');
    }

    /// <summary>The best matches for a query: types first among equals, then shorter names.</summary>
    public List<CodeMatch> Search(string query, CancellationToken cancel = default)
    {
        query = query.Trim();
        var snapshot = _snapshot;
        var matches = new List<CodeMatch>();
        if (query.Length == 0)
        {
            return matches;
        }

        void Try(CodeSymbol symbol)
        {
            if (FuzzyMatch.TryMatch(symbol.Name, query, out var score, out var positions))
            {
                score += symbol.IsType ? 20 : symbol.Kind == SymbolKind.File ? -5 : 0;
                matches.Add(new CodeMatch(symbol, score, positions));
            }
        }

        for (var i = 0; i < snapshot.Symbols.Length; i++)
        {
            if ((i & 4095) == 0)
            {
                cancel.ThrowIfCancellationRequested();
            }

            Try(snapshot.Symbols[i]);
        }

        foreach (var file in snapshot.Files)
        {
            Try(file);
        }

        return matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Symbol.Name.Length)
            .ThenBy(m => m.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Symbol.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .ToList();
    }

    // Git's files (tracked and untracked, not ignored) in a repository; otherwise a walk that leaves out build output.
    internal static async Task<List<string>> ListFilesAsync(string root)
    {
        if (await GitInfo.RunAsync(root, "ls-files", "-z", "-co", "--exclude-standard") is { } text)
        {
            return text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => Path.GetFullPath(Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxFiles)
                .ToList();
        }

        return await Task.Run(() =>
        {
            var files = new List<string>();
            var folders = new Stack<string>([root]);
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
            while (folders.Count > 0 && files.Count < MaxFiles)
            {
                var folder = folders.Pop();
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(folder, "*", options))
                    {
                        var name = Path.GetFileName(dir);
                        if (!DirectoryListing.IsHidden(name) && !DirectoryListing.IsExcluded(name, isDirectory: true))
                        {
                            folders.Push(dir);
                        }
                    }

                    files.AddRange(Directory.EnumerateFiles(folder, "*", options).Where(f => !DirectoryListing.IsExcluded(Path.GetFileName(f), isDirectory: false)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            return files;
        });
    }
}
