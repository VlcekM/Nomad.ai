using System.Collections.Concurrent;
using System.Text;

namespace Klodi.Core.Sessions;

/// <summary>A session as listed in the sidebar.</summary>
public sealed record SessionSummary(
    string SessionId,
    string FilePath,
    string? WorkingDirectory,
    string Title,
    DateTime LastActivityUtc,
    long SizeBytes);

/// <summary>
/// Lists and loads Claude Code sessions from <c>~/.claude/projects</c>. Summaries are read from the
/// head and tail of each transcript only, and cached by (size, mtime), so rescans are cheap.
/// </summary>
public sealed class SessionStore
{
    public const int HeadBytes = 256 * 1024;
    public const int TailBytes = 128 * 1024;
    public const long DefaultHistoryBytes = 4 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, (long Size, DateTime Mtime, SessionSummary? Summary)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SessionStore(string? projectsRoot = null)
    {
        ProjectsRoot = projectsRoot ?? DefaultProjectsRoot;
    }

    public static string DefaultProjectsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public string ProjectsRoot { get; }

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
                continue;
            }

            foreach (var file in files)
            {
                var summary = GetSummary(file);
                if (summary is not null)
                {
                    result.Add(summary);
                }
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

        return new SessionSummary(sessionId, file, builder.Cwd, title, mtimeUtc, size);
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

    private static FileStream OpenShared(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

    private static IEnumerable<string> ReadLines(FileStream stream, long start, long length, bool skipFirstPartial)
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

        public void Add(string line)
        {
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
