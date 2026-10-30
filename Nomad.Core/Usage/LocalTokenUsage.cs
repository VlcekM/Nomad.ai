using System.Text.Json;

namespace Nomad.Core.Usage;

/// <summary>Token totals recorded on this computer during a calendar month, including cached input.</summary>
public readonly record struct LocalTokens(long Input, long Output);

public static class LocalTokenUsage
{
    public static LocalTokens Claude(string root, DateTimeOffset since) => Read(root, since, codex: false);

    public static LocalTokens Codex(string root, DateTimeOffset since)
    {
        var sessions = Read(Path.Combine(root, "sessions"), since, codex: true);
        var archived = Read(Path.Combine(root, "archived_sessions"), since, codex: true);
        return new(sessions.Input + archived.Input, sessions.Output + archived.Output);
    }

    private static LocalTokens Read(string root, DateTimeOffset since, bool codex)
    {
        if (!Directory.Exists(root)) return default;
        long input = 0, output = 0;
        var messages = new Dictionary<string, LocalTokens>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < since.UtcDateTime) continue;
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    long previousInput = 0, previousOutput = 0;
                    string? line;
                    var lineNumber = 0;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        lineNumber++;
                        if (codex ? !line.Contains("token_count", StringComparison.Ordinal)
                                  : !line.Contains("input_tokens", StringComparison.Ordinal) || !line.Contains("usage", StringComparison.Ordinal)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var row = doc.RootElement;
                            if (!DateTimeOffset.TryParse(Text(row, "timestamp"), out var at)) continue;
                            if (codex)
                            {
                                var payload = Obj(row, "payload");
                                if (Text(row, "type") != "event_msg" || Text(payload, "type") != "token_count") continue;
                                var total = Obj(Obj(payload, "info"), "total_token_usage");
                                if (total.ValueKind != JsonValueKind.Object) continue;
                                var currentInput = Number(total, "input_tokens");
                                var currentOutput = Number(total, "output_tokens");
                                if (at >= since)
                                {
                                    input += currentInput >= previousInput ? currentInput - previousInput : currentInput;
                                    output += currentOutput >= previousOutput ? currentOutput - previousOutput : currentOutput;
                                }
                                previousInput = currentInput;
                                previousOutput = currentOutput;
                            }
                            else if (at >= since && Text(row, "type") == "assistant")
                            {
                                var message = Obj(row, "message");
                                var usage = Obj(message, "usage");
                                if (usage.ValueKind != JsonValueKind.Object) continue;
                                var key = Text(message, "id") ?? $"{file}:{lineNumber}";
                                var tokens = new LocalTokens(Number(usage, "input_tokens") + Number(usage, "cache_read_input_tokens") + Number(usage, "cache_creation_input_tokens"), Number(usage, "output_tokens"));
                                if (messages.TryGetValue(key, out var old)) tokens = new(Math.Max(old.Input, tokens.Input), Math.Max(old.Output, tokens.Output));
                                messages[key] = tokens;
                            }
                        }
                        catch (JsonException) { /* A partial or malformed line does not invalidate the rest. */ }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var tokens in messages.Values) { input += tokens.Input; output += tokens.Output; }
        return new(input, output);
    }

    private static JsonElement Obj(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) ? p : default;
    private static string? Text(JsonElement e, string name) => Obj(e, name) is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
    private static long Number(JsonElement e, string name) => Obj(e, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetInt64(out var n) ? Math.Max(0, n) : 0;
}
