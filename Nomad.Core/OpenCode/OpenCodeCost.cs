using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nomad.Core.OpenCode;

/// <summary>What one model ("provider/model") cost this month, in US dollars.</summary>
public sealed record CostLine(string Model, decimal Usd);

/// <summary>OpenCode's spending from the start of the month (local time) until now, by model, biggest first.</summary>
public sealed record CostReport(decimal TotalUsd, IReadOnlyList<CostLine> Lines, DateTimeOffset MonthStart, DateTimeOffset MonthEnd, DateTimeOffset At);

/// <summary>
/// Adds up what OpenCode spent this month from its own records: each assistant message carries the cost OpenCode
/// worked out for it (from the provider's token prices), so this needs no key. It counts every session on this PC,
/// subagents' too, and only OpenCode's use, not other use of the same API key.
/// <para>
/// Reading every session's messages is slow, so each session's sums are cached (<see cref="CachePath"/>) with the
/// time it was last updated: a refresh reads again only the sessions that changed since.
/// </para>
/// </summary>
public static class OpenCodeCost
{
    public static string CachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nomad", "opencode-cost-cache.json");

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<CostReport> MonthAsync(OpenCodeServer server, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
            var since = monthStart.ToUnixTimeMilliseconds();
            var cache = ReadCache(since);

            // Every session, subagents' included (they bill too); one touched this month may hold costs from it.
            using var list = await server.Http.GetAsync("experimental/session?limit=5000", cancellationToken).ConfigureAwait(false);
            list.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var wanted = new List<(string Id, long Updated)>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    var updated = OpenCodeJson.Long(OpenCodeJson.Obj(s, "time"), "updated") ?? 0;
                    if (OpenCodeJson.Str(s, "id") is { } id && updated >= since && (OpenCodeJson.Double(s, "cost") ?? 0) > 0)
                    {
                        wanted.Add((id, updated));
                    }
                }
            }

            var fresh = new Dictionary<string, SessionCost>(StringComparer.Ordinal);
            var stale = new List<(string Id, long Updated)>();
            foreach (var (id, updated) in wanted)
            {
                if (cache.TryGetValue(id, out var known) && known.Updated == updated)
                {
                    fresh[id] = known;
                }
                else
                {
                    stale.Add((id, updated));
                }
            }

            // A few at a time: the first read of a busy month may be dozens of sessions.
            await Parallel.ForEachAsync(stale, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (s, ct) =>
            {
                using var messages = await OpenCodeClient.GetMessagesAsync(server, null, s.Id, ct).ConfigureAwait(false);
                var sums = SumMessages(messages.RootElement, since);
                lock (fresh)
                {
                    fresh[s.Id] = new SessionCost(s.Updated, sums);
                }
            }).ConfigureAwait(false);

            if (stale.Count > 0 || fresh.Count != cache.Count)
            {
                WriteCache(since, fresh);
            }

            return Report(fresh.Values.Select(v => v.ByModel), monthStart, now);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A session's message list (<c>[{ info, parts }]</c>): its assistant messages' costs from <paramref name="since"/> on, by model.</summary>
    public static Dictionary<string, decimal> SumMessages(JsonElement messages, long since)
    {
        var sums = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return sums;
        }

        foreach (var m in messages.EnumerateArray())
        {
            var info = OpenCodeJson.Obj(m, "info");
            var cost = OpenCodeJson.Double(info, "cost") ?? 0;
            if (OpenCodeJson.Str(info, "role") != "assistant" || cost <= 0
                || (OpenCodeJson.Long(OpenCodeJson.Obj(info, "time"), "created") ?? 0) < since)
            {
                continue;
            }

            var model = $"{OpenCodeJson.Str(info, "providerID") ?? "unknown"}/{OpenCodeJson.Str(info, "modelID") ?? "unknown"}";
            sums[model] = sums.GetValueOrDefault(model) + (decimal)cost;
        }

        return sums;
    }

    public static CostReport Report(IEnumerable<IReadOnlyDictionary<string, decimal>> sessions, DateTimeOffset monthStart, DateTimeOffset now)
    {
        var byModel = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            foreach (var (model, usd) in session)
            {
                byModel[model] = byModel.GetValueOrDefault(model) + usd;
            }
        }

        var lines = byModel.Where(p => p.Value > 0).Select(p => new CostLine(p.Key, p.Value)).OrderByDescending(l => l.Usd).ToList();
        return new CostReport(lines.Sum(l => l.Usd), lines, monthStart, monthStart.AddMonths(1), now);
    }

    private sealed record SessionCost(long Updated, IReadOnlyDictionary<string, decimal> ByModel);

    /// <summary>The cached sums for this month; empty when there are none or they are another month's.</summary>
    private static Dictionary<string, SessionCost> ReadCache(long since)
    {
        var result = new Dictionary<string, SessionCost>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(CachePath))
            {
                return result;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            if (OpenCodeJson.Long(doc.RootElement, "since") != since || !doc.RootElement.TryGetProperty("sessions", out var sessions)
                || sessions.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var s in sessions.EnumerateObject())
            {
                var byModel = new Dictionary<string, decimal>(StringComparer.Ordinal);
                if (s.Value.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
                {
                    foreach (var m in models.EnumerateObject())
                    {
                        if (m.Value.ValueKind == JsonValueKind.String
                            && decimal.TryParse(m.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var usd))
                        {
                            byModel[m.Name] = usd;
                        }
                    }
                }

                result[s.Name] = new SessionCost(OpenCodeJson.Long(s.Value, "updated") ?? 0, byModel);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken cache only means reading the sessions again.
        }

        return result;
    }

    private static void WriteCache(long since, Dictionary<string, SessionCost> sessions)
    {
        try
        {
            var root = new JsonObject { ["since"] = since };
            var all = new JsonObject();
            foreach (var (id, cost) in sessions)
            {
                var models = new JsonObject();
                foreach (var (model, usd) in cost.ByModel)
                {
                    models[model] = usd.ToString(CultureInfo.InvariantCulture);
                }

                all[id] = new JsonObject { ["updated"] = cost.Updated, ["models"] = models };
            }

            root["sessions"] = all;
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not cached: the next refresh reads the sessions again.
        }
    }
}
