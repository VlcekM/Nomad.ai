using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nomad.Core.Usage;

/// <summary>One plan limit: the session (5-hour) window, the weekly one, or a weekly one for one model.</summary>
public sealed record PlanLimit(string Kind, string Label, int Percent, string Severity, DateTimeOffset? ResetsAt);

/// <summary>The plan's usage limits, as the claude.ai usage page shows them.</summary>
public sealed record PlanUsage(string? PlanName, IReadOnlyList<PlanLimit> Limits)
{
    public PlanLimit? Session => Limits.FirstOrDefault(l => l.Kind == "session");
}

/// <summary>
/// Reads plan usage the way Claude Code's <c>/usage</c> does: one read-only GET to the account's usage
/// endpoint with the CLI's stored claude.ai sign-in (<c>~/.claude/.credentials.json</c>). The token is
/// never refreshed here (the CLI does that) and never written anywhere. Best effort: any failure,
/// a missing or expired sign-in (API-key users), or a changed response gives null.
/// </summary>
public static partial class PlanUsageClient
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

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

            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Parse(json, PlanName(oauth.SubscriptionType, oauth.RateLimitTier));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
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
