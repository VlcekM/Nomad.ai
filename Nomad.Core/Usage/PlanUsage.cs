using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nomad.Core.Usage;

/// <summary>One plan limit: the session (5-hour) window, the weekly one, or a weekly one for one model.</summary>
public sealed record PlanLimit(string Kind, string Label, int Percent, string Severity, DateTimeOffset? ResetsAt);

/// <summary>
/// A weekly limit against an even pace: <see cref="Fraction"/> of the week has gone by (where using it up evenly,
/// around the clock, would be by now). From the rate so far, once enough of the week has gone by to tell: when it
/// <see cref="RunsOut"/> (before the reset), or else about how much is used by the reset (<see cref="ByReset"/>, %).
/// </summary>
public sealed record PlanPace(double Fraction, bool CanForecast, DateTimeOffset? RunsOut, double ByReset)
{
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    /// <summary>Under this much of the week, the rate so far says too little to forecast from.</summary>
    public static readonly TimeSpan MinForecastBasis = TimeSpan.FromHours(6);

    /// <summary>Null for a limit that is not weekly, has no reset time, or a reset outside the coming week.</summary>
    public static PlanPace? For(PlanLimit limit, DateTimeOffset now)
    {
        if (!limit.Kind.StartsWith("weekly", StringComparison.Ordinal) || limit.ResetsAt is not { } resets)
        {
            return null;
        }

        // The week runs up to the reset; an even pace is a straight line from 0 at its start to 100 % at its end.
        var start = resets - Week;
        var elapsed = now - start;
        if (elapsed <= TimeSpan.Zero || elapsed >= Week)
        {
            return null;
        }

        var fraction = elapsed / Week;
        if (elapsed < MinForecastBasis || limit.Percent <= 0)
        {
            return new PlanPace(fraction, elapsed >= MinForecastBasis, null, 0);
        }

        if (limit.Percent >= 100)
        {
            return new PlanPace(fraction, true, now, 100);
        }

        var runsOut = start + (elapsed * (100.0 / limit.Percent));
        return runsOut < resets
            ? new PlanPace(fraction, true, runsOut, 100)
            : new PlanPace(fraction, true, null, Math.Min(100, limit.Percent * (Week / elapsed)));
    }
}

/// <summary>
/// The plan's usage limits, as the claude.ai usage page shows them. <see cref="StaleSince"/> is set when the
/// endpoint could not be reached and these are the last ones read (then from that time).
/// </summary>
public sealed record PlanUsage(string? PlanName, IReadOnlyList<PlanLimit> Limits, DateTimeOffset? StaleSince = null)
{
    public PlanLimit? Session => Limits.FirstOrDefault(l => l.Kind == "session");
}

/// <summary>
/// Reads plan usage the way Claude Code's <c>/usage</c> does: one read-only GET to the account's usage
/// endpoint with the CLI's stored claude.ai sign-in (<c>~/.claude/.credentials.json</c>). The token is
/// never refreshed here (the CLI does that) and never written anywhere. Best effort: any failure,
/// a missing or expired sign-in (API-key users), or a changed response gives null.
/// <para>
/// The last answer is kept in <c>%APPDATA%\Nomad\usage-cache.json</c> (percentages and reset times only). Every
/// Nomad window shares it, so a fresh one is used instead of asking again (the endpoint rate-limits hard), and an
/// older one stands in, marked stale, when the endpoint refuses.
/// </para>
/// </summary>
public static partial class PlanUsageClient
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>A cached answer this young is used as it is.</summary>
    private static readonly TimeSpan CacheFresh = TimeSpan.FromSeconds(50);

    /// <summary>A cached answer this young stands in when the endpoint refuses.</summary>
    private static readonly TimeSpan CacheUsable = TimeSpan.FromHours(6);

    public static string CachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nomad", "usage-cache.json");

    public static string CredentialsPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            ".credentials.json");

    public static async Task<PlanUsage?> FetchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(CredentialsPath))
            {
                return null;
            }

            var credentials = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(CredentialsPath, cancellationToken), UsageJsonContext.Default.CredentialsFile);
            if (credentials?.ClaudeAiOauth is not { AccessToken: { Length: > 0 } token } oauth
                || DateTimeOffset.FromUnixTimeMilliseconds(oauth.ExpiresAt) <= DateTimeOffset.UtcNow)
            {
                return null;
            }

            var planName = PlanName(oauth.SubscriptionType, oauth.RateLimitTier);
            var cached = ReadCache();
            if (cached is { } fresh && DateTimeOffset.UtcNow - fresh.At < CacheFresh)
            {
                return Parse(fresh.Json, planName);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            string? json = null;
            try
            {
                using var response = await Http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    json = await response.Content.ReadAsStringAsync(cancellationToken);
                }
            }
            catch (HttpRequestException)
            {
                // Offline: the cache may still do.
            }

            if (json is not null && Parse(json, planName) is { } usage)
            {
                WriteCache(json);
                return usage;
            }

            return cached is { } old && DateTimeOffset.UtcNow - old.At < CacheUsable
                ? Parse(old.Json, planName) is { } stale ? stale with { StaleSince = old.At } : null
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static (string Json, DateTimeOffset At)? ReadCache()
    {
        try
        {
            var file = new FileInfo(CachePath);
            return file.Exists ? (File.ReadAllText(file.FullName), new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteCache(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temp = CachePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another window wrote it at the same moment, or no access: not needed.
        }
    }

    /// <summary>The response's <c>limits</c> list, in the server's order.</summary>
    public static PlanUsage? Parse(string json, string? planName)
    {
        var body = JsonSerializer.Deserialize(json, UsageJsonContext.Default.UsageResponse);
        if (body?.Limits is not { } limits)
        {
            return null;
        }

        var list = limits
            .Where(l => !string.IsNullOrEmpty(l.Kind))
            .Select(l => new PlanLimit(l.Kind!, Label(l), (int)Math.Round(l.Percent), l.Severity ?? "normal", l.ResetsAt))
            .ToList();
        return new PlanUsage(planName, list);
    }

    private static string Label(UsageLimit limit) => limit.Kind switch
    {
        "session" => "Session limit",
        "weekly_all" => "Weekly · all models",
        "weekly_scoped" when limit.Scope?.Model?.DisplayName is { Length: > 0 } model => $"Weekly · {model}",
        _ when limit.Group is { Length: > 0 } group => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(group.Replace('_', ' ')),
        _ => limit.Kind!.Replace('_', ' '),
    };

    /// <summary>"Max (5x)" from <c>max</c> + <c>default_claude_max_5x</c>; "Pro"; null when unknown.</summary>
    public static string? PlanName(string? subscriptionType, string? rateLimitTier)
    {
        if (string.IsNullOrEmpty(subscriptionType))
        {
            return null;
        }

        var name = char.ToUpperInvariant(subscriptionType[0]) + subscriptionType[1..];
        return rateLimitTier is not null && Multiplier().Match(rateLimitTier) is { Success: true } m
            ? $"{name} ({m.Groups[1].Value}x)"
            : name;
    }

    [GeneratedRegex(@"_(\d+)x$")]
    private static partial Regex Multiplier();
}

internal sealed class CredentialsFile
{
    [JsonPropertyName("claudeAiOauth")]
    public OAuthCredentials? ClaudeAiOauth { get; set; }
}

internal sealed class OAuthCredentials
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expiresAt")]
    public long ExpiresAt { get; set; }

    [JsonPropertyName("subscriptionType")]
    public string? SubscriptionType { get; set; }

    [JsonPropertyName("rateLimitTier")]
    public string? RateLimitTier { get; set; }
}

internal sealed class UsageResponse
{
    [JsonPropertyName("limits")]
    public List<UsageLimit>? Limits { get; set; }
}

internal sealed class UsageLimit
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }

    [JsonPropertyName("percent")]
    public double Percent { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("resets_at")]
    public DateTimeOffset? ResetsAt { get; set; }

    [JsonPropertyName("scope")]
    public UsageScope? Scope { get; set; }
}

internal sealed class UsageScope
{
    [JsonPropertyName("model")]
    public UsageModel? Model { get; set; }
}

internal sealed class UsageModel
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(CredentialsFile))]
[JsonSerializable(typeof(UsageResponse))]
internal sealed partial class UsageJsonContext : JsonSerializerContext;
