using System.Text.Json;
using Nomad.Core.Agents;
using Nomad.Hub.Contracts;

namespace Nomad.Core.Hub;

/// <summary>
/// Per-call token records from session files (docs/hub.md §7). Input is always the uncached part, as Anthropic counts
/// it; OpenAI's input (which includes the cached part) is split to match.
/// </summary>
public static class UsageExtractor
{
    /// <summary>
    /// Claude Code: one record per <c>message.id</c> of the assistant lines (a reply split into several lines repeats its
    /// usage; the largest counts win).
    /// </summary>
    public static IReadOnlyList<UsageRecordDto> Claude(string sessionId, string transcript)
    {
        var byId = new Dictionary<string, UsageRecordDto>();
        foreach (var row in Lines(transcript))
        {
            if (Text(row, "type") != "assistant" || Obj(row, "message") is not { ValueKind: JsonValueKind.Object } message
                || Obj(message, "usage") is not { ValueKind: JsonValueKind.Object } usage
                || Text(message, "id") is not { } id
                || !DateTime.TryParse(Text(row, "timestamp"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
            {
                continue;
            }

            var model = Text(message, "model");
            if (model is "<synthetic>")
            {
                continue;
            }

            var record = new UsageRecordDto(AgentBackends.ClaudeCode, sessionId, id, "anthropic", model, at,
                Number(usage, "input_tokens"), Number(usage, "output_tokens"), 0,
                Number(usage, "cache_read_input_tokens"), Number(usage, "cache_creation_input_tokens"), null);
            if (byId.TryGetValue(id, out var old))
            {
                record = record with
                {
                    AtUtc = old.AtUtc,
                    Input = Math.Max(old.Input, record.Input),
                    Output = Math.Max(old.Output, record.Output),
                    CacheRead = Math.Max(old.CacheRead, record.CacheRead),
                    CacheWrite = Math.Max(old.CacheWrite, record.CacheWrite),
                };
            }

            byId[id] = record;
        }

        return byId.Values.ToList();
    }

    /// <summary>Codex: each <c>token_count</c> event's <c>last_token_usage</c>, keyed by its ordinal in the rollout.</summary>
    public static IReadOnlyList<UsageRecordDto> Codex(string sessionId, string rollout)
    {
        var result = new List<UsageRecordDto>();
        string? model = null;
        var index = 0;
        long previousTotal = -1;
        foreach (var row in Lines(rollout))
        {
            index++;
            var payload = Obj(row, "payload");
            if (Text(row, "type") == "turn_context")
            {
                model = Text(payload, "model") ?? model;
                continue;
            }

            if (Text(row, "type") != "event_msg" || Text(payload, "type") != "token_count"
                || Obj(Obj(payload, "info"), "last_token_usage") is not { ValueKind: JsonValueKind.Object } last
                || !DateTime.TryParse(Text(row, "timestamp"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
            {
                continue;
            }

            // An event that repeats the last one (the running total didn't move) is no new call.
            var total = Number(Obj(Obj(payload, "info"), "total_token_usage"), "total_tokens");
            if (total == previousTotal)
            {
                continue;
            }

            previousTotal = total;
            var key = Obj(row, "ordinal") is { ValueKind: JsonValueKind.Number } o ? $"o{o.GetInt64()}" : $"l{index}:{at:O}";
            var input = Number(last, "input_tokens");
            var cached = Number(last, "cached_input_tokens");
            result.Add(new UsageRecordDto(AgentBackends.Codex, sessionId, key, "openai", model, at,
                Math.Max(0, input - cached), Number(last, "output_tokens"), Number(last, "reasoning_output_tokens"),
                cached, Number(last, "cache_write_input_tokens"), null));
        }

        return result;
    }

    /// <summary>OpenCode: each assistant message of an <c>opencode export</c>, with OpenCode's own cost.</summary>
    public static IReadOnlyList<UsageRecordDto> OpenCode(string sessionId, string export)
    {
        var result = new List<UsageRecordDto>();
        try
        {
            using var stream = File.OpenRead(export);
            using var doc = JsonDocument.Parse(stream);
            if (Obj(doc.RootElement, "messages") is not { ValueKind: JsonValueKind.Array } messages)
            {
                return result;
            }

            foreach (var m in messages.EnumerateArray())
            {
                var info = Obj(m, "info");
                if (Text(info, "role") != "assistant" || Text(info, "id") is not { } id || Obj(info, "tokens") is not { ValueKind: JsonValueKind.Object } tokens)
                {
                    continue;
                }

                var created = Obj(Obj(info, "time"), "created") is { ValueKind: JsonValueKind.Number } c && c.TryGetInt64(out var ms) ? ms : 0;
                var cache = Obj(tokens, "cache");
                result.Add(new UsageRecordDto(AgentBackends.OpenCode, sessionId, id, Text(info, "providerID"), Text(info, "modelID"),
                    DateTimeOffset.FromUnixTimeMilliseconds(created).UtcDateTime,
                    Number(tokens, "input"), Number(tokens, "output"), Number(tokens, "reasoning"), Number(cache, "read"), Number(cache, "write"),
                    Obj(info, "cost") is { ValueKind: JsonValueKind.Number } cost ? cost.GetDouble() : null));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return result;
    }

    private static IEnumerable<JsonElement> Lines(string file)
    {
        List<string> lines;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = [];
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            if (!line.Contains("usage", StringComparison.Ordinal) && !line.Contains("turn_context", StringComparison.Ordinal))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                yield return doc.RootElement;
            }
        }
    }

    private static JsonElement Obj(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) ? p : default;

    private static string? Text(JsonElement e, string name) => Obj(e, name) is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;

    private static long Number(JsonElement e, string name) => Obj(e, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetInt64(out var n) ? Math.Max(0, n) : 0;
}
