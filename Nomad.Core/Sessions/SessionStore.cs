using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nomad.Core.Sessions;

/// <summary>A session as listed in the sidebar.</summary>
public sealed record SessionSummary(
    string SessionId,
    string FilePath,
    string? WorkingDirectory,
    string Title,
    DateTime LastActivityUtc,
    long SizeBytes,
    string? GitBranch = null,
    string? PullRequestUrl = null,
    string? IssueUrl = null);

/// <summary>
/// Lists and loads Claude Code sessions from <c>~/.claude/projects</c>. Summaries are read from the
/// head and tail of each transcript only, and cached by (size, mtime), so rescans are cheap. With a
/// <see cref="CachePath"/> the cache is kept between runs, so a start reads only the transcripts that changed.
/// </summary>
public sealed class SessionStore
{
    public const int HeadBytes = 256 * 1024;
    public const int TailBytes = 128 * 1024;
    public const long DefaultHistoryBytes = 4 * 1024 * 1024;

    // Bump when ReadSummary learns something new: summaries saved by an older version are then read again.
    private const int CacheVersion = 1;

    private readonly ConcurrentDictionary<string, (long Size, DateTime Mtime, SessionSummary? Summary)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _saveLock = new();
    private volatile bool _cacheDirty;

    public SessionStore(string? projectsRoot = null, string? cachePath = null)
    {
        ProjectsRoot = projectsRoot ?? DefaultProjectsRoot;
        CachePath = cachePath;
    }

    public static string DefaultProjectsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public string ProjectsRoot { get; }

    /// <summary>The file the summaries are kept in between runs; null keeps them in memory only.</summary>
    public string? CachePath { get; }

    /// <summary>
    /// The summaries the last run saved, most recently active first, without opening a transcript (they may be
    /// out of date until the next <see cref="Scan"/>). They also seed the cache. Safe to call from a background thread.
    /// </summary>
    public IReadOnlyList<SessionSummary> LoadCache()
    {
        var result = new List<SessionSummary>();
        if (CachePath is null)
        {
            return result;
        }

        SessionCacheFile? saved;
        try
        {
            using var stream = File.OpenRead(CachePath);
            saved = JsonSerializer.Deserialize(stream, SessionCacheJsonContext.Default.SessionCacheFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return result;
        }

        if (saved is not { Version: CacheVersion })
        {
            return result;
        }

        foreach (var entry in saved.Entries)
        {
            var mtime = new DateTime(entry.MtimeTicks, DateTimeKind.Utc);
            if (_cache.TryAdd(entry.File, (entry.Size, mtime, entry.Summary)) && entry.Summary is { } summary)
            {
                result.Add(summary);
            }
        }

        result.Sort((a, b) => b.LastActivityUtc.CompareTo(a.LastActivityUtc));
        return result;
    }

    /// <summary>Writes the cache to <see cref="CachePath"/> if it changed since it was loaded or last saved.</summary>
    public void SaveCache()
    {
        if (CachePath is null || !_cacheDirty)
        {
            return;
        }

        lock (_saveLock)
        {
            _cacheDirty = false;
            var file = new SessionCacheFile
            {
                Version = CacheVersion,
                Entries = _cache.Select(e => new SessionCacheEntry(e.Key, e.Value.Size, e.Value.Mtime.Ticks, e.Value.Summary)).ToList(),
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(CachePath))!);
                var temp = CachePath + "." + Environment.ProcessId + ".tmp";
                using (var stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, file, SessionCacheJsonContext.Default.SessionCacheFile);
                }

                File.Move(temp, CachePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _cacheDirty = true; // Another Nomad.ai writing it at the same moment: the next scan tries again.
            }
        }
    }

    /// <summary>All sessions, most recently active first. Safe to call from a background thread.</summary>
    public IReadOnlyList<SessionSummary> Scan(CancellationToken cancellationToken = default)
    {
        var result = new List<SessionSummary>();
        if (!Directory.Exists(ProjectsRoot))
        {
            return result;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(ProjectsRoot).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        foreach (var dir in dirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }

            foreach (var file in files)
            {
                seen.Add(file);
                var summary = GetSummary(file);
                if (summary is not null)
                {
                    result.Add(summary);
                }
            }
        }

        // Transcripts deleted since: out of the cache (only after a full listing, so a folder that could not be read
        // keeps its entries).
        if (complete)
        {
            foreach (var gone in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _cache.TryRemove(gone, out _);
                _cacheDirty = true;
            }
        }

        result.Sort((a, b) => b.LastActivityUtc.CompareTo(a.LastActivityUtc));
        return result;
    }

    /// <summary>The cached or freshly read summary of one transcript; null for empty sessions.</summary>
    public SessionSummary? GetSummary(string file)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (_cache.TryGetValue(file, out var cached) && cached.Size == info.Length && cached.Mtime == info.LastWriteTimeUtc)
        {
            return cached.Summary;
        }

        SessionSummary? summary;
        try
        {
            summary = ReadSummary(file, info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            summary = null;
        }

        _cache[file] = (info.Length, info.LastWriteTimeUtc, summary);
        _cacheDirty = true;
        return summary;
    }

    internal static SessionSummary? ReadSummary(string file, long size, DateTime mtimeUtc)
    {
        using var stream = OpenShared(file);
        var builder = new SummaryBuilder();

        foreach (var line in ReadLines(stream, 0, Math.Min(size, HeadBytes), skipFirstPartial: false))
        {
            builder.Add(line);
        }

        if (size > HeadBytes)
        {
            var tailStart = Math.Max(HeadBytes, size - TailBytes);
            foreach (var line in ReadLines(stream, tailStart, size - tailStart, skipFirstPartial: true))
            {
                builder.AddTail(line);
            }
        }

        var sessionId = builder.SessionId ?? Path.GetFileNameWithoutExtension(file);
        var title = builder.Title;
        if (title is null)
        {
            return null;
        }

        return new SessionSummary(sessionId, file, builder.Cwd, title, mtimeUtc, size, builder.GitBranch, builder.PullRequestUrl, builder.IssueUrl);
    }

    /// <summary>
    /// Loads the conversation for display. Large transcripts are read from the end only
    /// (<paramref name="maxBytes"/>), and the history starts with a "truncated" notice.
    /// </summary>
    public static SessionHistory LoadHistory(string file, long maxBytes = DefaultHistoryBytes)
    {
        using var stream = OpenShared(file);
        var size = stream.Length;
        var truncated = size > maxBytes;
        var start = truncated ? size - maxBytes : 0;
        var records = new List<TranscriptRecord>();
        foreach (var line in ReadLines(stream, start, size - start, skipFirstPartial: truncated))
        {
            var record = TranscriptRecord.Parse(line);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return SessionHistory.FromRecords(records, truncated, Path.GetFileNameWithoutExtension(file));
    }

    /// <summary>
    /// Renames a session the way Claude Code's <c>/rename</c> does: a <c>custom-title</c> record appended to its
    /// transcript (the last one wins). The only write Nomad makes to a transcript.
    /// </summary>
    public static void AppendCustomTitle(string file, string sessionId, string title)
    {
        using var buffer = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "custom-title");
            json.WriteString("customTitle", title);
            json.WriteString("sessionId", sessionId);
            json.WriteEndObject();
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        // Start on a line of its own, even if the last record has no newline yet.
        var newline = new[] { (byte)'\n' };
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.Write(newline);
            }
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(buffer.ToArray());
        stream.Write(newline);
    }

    /// <summary>
    /// The record before <paramref name="uuid"/> in a transcript (its <c>parentUuid</c>): where a
    /// rewind to just before that message resumes. Null when the record is the first, or not found.
    /// </summary>
    public static string? ParentOf(string file, string uuid)
    {
        using var stream = OpenShared(file);
        var needle = $"\"uuid\":\"{uuid}\"";
        foreach (var line in ReadLines(stream, 0, stream.Length, skipFirstPartial: false))
        {
            if (line.Contains(needle, StringComparison.Ordinal) && TranscriptRecord.Parse(line) is { } record && record.Uuid == uuid)
            {
                return record.ParentUuid ?? record.LogicalParentUuid;
            }
        }

        return null;
    }

    /// <summary>Finds the transcript file of a session id (any project folder).</summary>
    public string? FindTranscript(string sessionId)
    {
        if (!Directory.Exists(ProjectsRoot))
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(ProjectsRoot))
        {
            var candidate = Path.Combine(dir, sessionId + ".jsonl");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static FileStream OpenShared(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

    internal static IEnumerable<string> ReadLines(FileStream stream, long start, long length, bool skipFirstPartial)
    {
        stream.Seek(start, SeekOrigin.Begin);
        var limited = new LimitedStream(stream, length);
        using var reader = new StreamReader(limited, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var first = true;
        while (reader.ReadLine() is { } line)
        {
            if (first && skipFirstPartial)
            {
                first = false;
                continue;
            }

            first = false;
            yield return line;
        }
    }

    private sealed class SummaryBuilder
    {
        private string? _firstPrompt;
        private string? _customTitle;
        private string? _aiTitle;
        private string? _summary;

        public string? SessionId { get; private set; }

        public string? Cwd { get; private set; }

        public string? Title =>
            !string.IsNullOrWhiteSpace(_customTitle) ? SessionTitles.Shorten(_customTitle) :
            !string.IsNullOrWhiteSpace(_aiTitle) ? SessionTitles.Shorten(_aiTitle) :
            !string.IsNullOrWhiteSpace(_summary) ? SessionTitles.Shorten(_summary) :
            _firstPrompt is not null ? SessionTitles.Shorten(_firstPrompt) : null;

        /// <summary>The branch of the newest record that names one (records carry the checkout they were made on).</summary>
        public string? GitBranch { get; private set; }

        /// <summary>The last GitHub pull request the session mentions (one it opened, or was asked about).</summary>
        public string? PullRequestUrl { get; private set; }

        /// <summary>The last GitHub issue the session mentions.</summary>
        public string? IssueUrl { get; private set; }

        private static readonly System.Text.RegularExpressions.Regex GitHubUrl = new(
            @"https://github\.com/([\w.-]+)/([\w.-]+)/(pull|issues)/(\d+)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private void NoteLinks(string line)
        {
            if (!line.Contains("github.com/", StringComparison.Ordinal))
            {
                return;
            }

            foreach (System.Text.RegularExpressions.Match m in GitHubUrl.Matches(line))
            {
                var url = $"https://github.com/{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}/{m.Groups[4].Value}";
                if (m.Groups[3].Value == "pull")
                {
                    PullRequestUrl = url;
                }
                else
                {
                    IssueUrl = url;
                }
            }
        }

        private void NoteBranch(string line)
        {
            NoteLinks(line);
            const string key = "\"gitBranch\":\"";
            var at = line.LastIndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                return;
            }

            var start = at + key.Length;
            var end = line.IndexOf('"', start);
            if (end > start)
            {
                GitBranch = line[start..end];
            }
        }

        public void Add(string line)
        {
            NoteBranch(line);
            if (!MightMatter(line))
            {
                return;
            }

            var record = TranscriptRecord.Parse(line);
            if (record is null)
            {
                return;
            }

            if (Cwd is null && !string.IsNullOrEmpty(record.Cwd))
            {
                Cwd = record.Cwd;
                SessionId ??= record.SessionId;
            }

            switch (record.Type)
            {
                case "custom-title" when !string.IsNullOrWhiteSpace(record.CustomTitle):
                    _customTitle = record.CustomTitle;
                    break;
                case "ai-title" when !string.IsNullOrWhiteSpace(record.AiTitle):
                    _aiTitle = record.AiTitle;
                    break;
                case "summary" when !string.IsNullOrWhiteSpace(record.Summary):
                    _summary ??= record.Summary;
                    break;
                case "user" when _firstPrompt is null:
                    _firstPrompt = SessionTitles.PromptText(record);
                    break;
            }
        }

        public void AddTail(string line)
        {
            NoteBranch(line);
            // Titles are re-appended as the session goes on; the last one wins.
            if (line.Contains("\"custom-title\"", StringComparison.Ordinal) || line.Contains("\"ai-title\"", StringComparison.Ordinal))
            {
                var record = TranscriptRecord.Parse(line);
                if (record is { Type: "custom-title" } && !string.IsNullOrWhiteSpace(record.CustomTitle))
                {
                    _customTitle = record.CustomTitle;
                }
                else if (record is { Type: "ai-title" } && !string.IsNullOrWhiteSpace(record.AiTitle))
                {
                    _aiTitle = record.AiTitle;
                }
            }
        }

        private bool MightMatter(string line) =>
            Cwd is null || _firstPrompt is null ||
            line.Contains("\"custom-title\"", StringComparison.Ordinal) ||
            line.Contains("\"ai-title\"", StringComparison.Ordinal) ||
            line.Contains("\"summary\"", StringComparison.Ordinal);
    }

    /// <summary>A read-only window over another stream.</summary>
    private sealed class LimitedStream(Stream inner, long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>The summaries cache as saved between runs (<see cref="SessionStore.CachePath"/>).</summary>
internal sealed class SessionCacheFile
{
    public int Version { get; set; }

    public List<SessionCacheEntry> Entries { get; set; } = [];
}

/// <summary>One transcript: its size and write time when read, and its summary (null for an empty session).</summary>
internal sealed record SessionCacheEntry(string File, long Size, long MtimeTicks, SessionSummary? Summary);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionCacheFile))]
internal sealed partial class SessionCacheJsonContext : JsonSerializerContext;
