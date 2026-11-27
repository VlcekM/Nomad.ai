using System.Text.Json;

namespace Nomad.Core.Usage;

/// <summary>Token totals recorded on this computer during a calendar month, including cached input.</summary>
public readonly record struct LocalTokens(long Input, long Output)
{
    public static LocalTokens operator +(LocalTokens left, LocalTokens right) =>
        new(left.Input + right.Input, left.Output + right.Output);

    internal static LocalTokens Max(LocalTokens left, LocalTokens right) =>
        new(Math.Max(left.Input, right.Input), Math.Max(left.Output, right.Output));
}

public static class LocalTokenUsage
{
    public static LocalTokens Claude(string root, DateTimeOffset since)
    {
        var messages = new Dictionary<string, LocalTokens>(StringComparer.Ordinal);
        Read([root], since, codex: false, (key, tokens) =>
            messages[key] = LocalTokens.Max(messages.GetValueOrDefault(key), tokens));
        return messages.Values.Aggregate(default(LocalTokens), (sum, tokens) => sum + tokens);
    }

    public static LocalTokens Codex(string root, DateTimeOffset since) =>
        Read(CodexRoots(root), since, codex: true, static (_, _) => { });

    internal static string[] CodexRoots(string root) =>
        [Path.Combine(root, "sessions"), Path.Combine(root, "archived_sessions")];

    internal static IEnumerable<string> Files(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
        : [];

    private static LocalTokens Read(IEnumerable<string> roots, DateTimeOffset since, bool codex, Action<string, LocalTokens> record)
    {
        var total = default(LocalTokens);
        foreach (var root in roots)
        {
            try
            {
                foreach (var file in Files(root))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < since.UtcDateTime) continue;
                        using var stream = Open(file);
                        total += Scan(stream, stream.Length, file, since, codex, default, record).Total;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    internal static FileStream Open(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal readonly record struct Cursor(long Position, bool Complete, LocalTokens Previous, LocalTokens Total);

    internal static Cursor Scan(FileStream stream, long length, string path, DateTimeOffset since, bool codex,
        Cursor cursor, Action<string, LocalTokens> record)
    {
        stream.Position = cursor.Position;
        var reader = new TokenLogReader(stream, length, codex);
        var total = cursor.Total;
        var previous = cursor.Previous;
        var complete = cursor.Complete;
        while (reader.Position < length)
        {
            var position = reader.Position;
            using var doc = reader.ReadLine();
            if (reader.Position == position) throw new EndOfStreamException();
            complete = reader.EndedWithNewline;
            if (doc is null) continue;

            var row = doc.RootElement;
            if (!DateTimeOffset.TryParse(Text(row, "timestamp"), out var at)) continue;
            if (codex)
            {
                var payload = Obj(row, "payload");
                if (Text(row, "type") != "event_msg" || Text(payload, "type") != "token_count") continue;
                var usage = Obj(Obj(payload, "info"), "total_token_usage");
                if (usage.ValueKind != JsonValueKind.Object) continue;

                var current = new LocalTokens(Number(usage, "input_tokens"), Number(usage, "output_tokens"));
                if (at >= since)
                {
                    total += new LocalTokens(
                        current.Input >= previous.Input ? current.Input - previous.Input : current.Input,
                        current.Output >= previous.Output ? current.Output - previous.Output : current.Output);
                }
                previous = current;
            }
            else if (at >= since && Text(row, "type") == "assistant")
            {
                var message = Obj(row, "message");
                var usage = Obj(message, "usage");
                if (usage.ValueKind != JsonValueKind.Object) continue;

                var key = Text(message, "id") ?? $"{path}:{position}";
                record(key, new LocalTokens(Number(usage, "input_tokens") + Number(usage, "cache_read_input_tokens")
                    + Number(usage, "cache_creation_input_tokens"), Number(usage, "output_tokens")));
            }
        }
        return new Cursor(reader.Position, complete, previous, total);
    }

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) ? p : default;

    private static string? Text(JsonElement e, string name) =>
        Obj(e, name) is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;

    private static long Number(JsonElement e, string name) =>
        Obj(e, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetInt64(out var n) ? n : 0;
}
