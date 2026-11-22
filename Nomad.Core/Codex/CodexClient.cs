using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nomad.Core.Codex;

/// <summary>A model Codex offers: its id, name, and the reasoning efforts it takes.</summary>
public sealed record CodexModel(string Id, string Name, string Description, IReadOnlyList<string> Efforts, string? DefaultEffort, bool IsDefault);

/// <summary>A stored Codex thread, as the session list shows it.</summary>
public sealed record CodexThreadInfo(string Id, string? Directory, string Title, DateTime UpdatedUtc);

/// <summary>One of Codex's usage windows: how much of it is used, how long it is, when it starts over.</summary>
public sealed record CodexLimitWindow(double UsedPercent, int? WindowMinutes, DateTime? ResetsAtUtc);

/// <summary>The ChatGPT plan's Codex limits: the short window (5 hours) and the long one (a week).</summary>
public sealed record CodexRateLimits(CodexLimitWindow? Primary, CodexLimitWindow? Secondary, string? PlanType);

/// <summary>Calls on the <see cref="CodexServer"/> that aren't a session's own: models, stored threads, limits.</summary>
public static class CodexClient
{
    /// <summary>The models Codex offers the account, in its order.</summary>
    public static async Task<IReadOnlyList<CodexModel>> ListModelsAsync(CodexServer server, CancellationToken cancellationToken = default)
    {
        var models = new List<CodexModel>();
        string? cursor = null;
        do
        {
            var result = await server.CallAsync("model/list", new JsonObject { ["cursor"] = cursor, ["limit"] = 100 }, cancellationToken).ConfigureAwait(false);
            foreach (var m in CodexJson.Arr(result, "data"))
            {
                if (CodexJson.Str(m, "id") is not { } id || CodexJson.Bool(m, "hidden") == true)
                {
                    continue;
                }

                var efforts = CodexJson.Arr(m, "supportedReasoningEfforts").Select(e => CodexJson.Str(e, "reasoningEffort")).OfType<string>().ToList();
                models.Add(new CodexModel(
                    id,
                    CodexJson.Str(m, "displayName") ?? id,
                    CodexJson.Str(m, "description") ?? "",
                    efforts,
                    CodexJson.Str(m, "defaultReasoningEffort"),
                    CodexJson.Bool(m, "isDefault") == true));
            }

            cursor = CodexJson.Str(result, "nextCursor");
        }
        while (cursor is not null && models.Count < 500);

        return models;
    }

    /// <summary>The stored threads started by a person (the CLI, the IDE extension, apps like this one), newest first.</summary>
    public static async Task<IReadOnlyList<CodexThreadInfo>> ListThreadsAsync(CodexServer server, int max = 300, CancellationToken cancellationToken = default)
    {
        var threads = new List<CodexThreadInfo>();
        string? cursor = null;
        do
        {
            var result = await server.CallAsync("thread/list", new JsonObject
            {
                ["cursor"] = cursor,
                ["limit"] = 100,
                ["sortKey"] = "updated_at",
                ["sourceKinds"] = new JsonArray("cli", "vscode", "exec", "appServer"),
            }, cancellationToken).ConfigureAwait(false);
            foreach (var t in CodexJson.Arr(result, "data"))
            {
                if (CodexJson.Str(t, "id") is { } id && CodexJson.Bool(t, "ephemeral") != true)
                {
                    threads.Add(new CodexThreadInfo(id, CodexJson.Str(t, "cwd"), Title(t), Time(CodexJson.Long(t, "updatedAt")) ?? DateTime.UtcNow));
                }
            }

            cursor = CodexJson.Str(result, "nextCursor");
        }
        while (cursor is not null && threads.Count < max);

        return threads;
    }

    /// <summary>A thread with its turns and their items (for its history).</summary>
    public static Task<JsonElement> ReadThreadAsync(CodexServer server, string threadId, CancellationToken cancellationToken = default) =>
        server.CallAsync("thread/read", new JsonObject { ["threadId"] = threadId, ["includeTurns"] = true }, cancellationToken);

    public static Task SetNameAsync(CodexServer server, string threadId, string name, CancellationToken cancellationToken = default) =>
        server.CallAsync("thread/name/set", new JsonObject { ["threadId"] = threadId, ["name"] = name }, cancellationToken);

    /// <summary>Moves a thread to Codex's archive (<c>~/.codex/archived_sessions</c>): out of every list, but not gone.</summary>
    public static Task ArchiveAsync(CodexServer server, string threadId, CancellationToken cancellationToken = default) =>
        server.CallAsync("thread/archive", new JsonObject { ["threadId"] = threadId }, cancellationToken);

    /// <summary>The plan's Codex limits right now.</summary>
    public static async Task<CodexRateLimits?> RateLimitsAsync(CodexServer server, CancellationToken cancellationToken = default)
    {
        var result = await server.CallAsync("account/rateLimits/read", null, cancellationToken).ConfigureAwait(false);
        return RateLimits(CodexJson.Obj(result, "rateLimits"));
    }

    /// <summary>The account Codex is signed in with: "chatgpt" with its plan, "apiKey", or null when signed out.</summary>
    public static async Task<(string? Type, string? Email, string? Plan)> AccountAsync(CodexServer server, CancellationToken cancellationToken = default)
    {
        var result = await server.CallAsync("account/read", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var account = CodexJson.Obj(result, "account");
        return (CodexJson.Str(account, "type"), CodexJson.Str(account, "email"), CodexJson.Str(account, "planType"));
    }

    /// <summary>Starts the ChatGPT subscription browser OAuth flow; returns the login id and URL to open.</summary>
    public static async Task<(string LoginId, string AuthUrl)> StartChatGptLoginAsync(CodexServer server, CancellationToken cancellationToken = default)
    {
        var result = await server.CallAsync("account/login/start", new JsonObject { ["type"] = "chatgpt" }, cancellationToken).ConfigureAwait(false);
        var loginId = CodexJson.Str(result, "loginId")
            ?? throw new InvalidOperationException("Codex did not return a login ID.");
        var authUrl = CodexJson.Str(result, "authUrl")
            ?? throw new InvalidOperationException("Codex did not return a sign-in URL.");
        return (loginId, authUrl);
    }

    /// <summary>Cancels an in-flight browser login.</summary>
    public static Task CancelLoginAsync(CodexServer server, string loginId, CancellationToken cancellationToken = default) =>
        server.CallAsync("account/login/cancel", new JsonObject { ["loginId"] = loginId }, cancellationToken);

    /// <summary>Signs Codex out, removing stored credentials.</summary>
    public static Task LogoutAsync(CodexServer server, CancellationToken cancellationToken = default) =>
        server.CallAsync("account/logout", new JsonObject(), cancellationToken);

    /// <summary>A <c>RateLimitSnapshot</c> (also what <c>account/rateLimits/updated</c> carries).</summary>
    public static CodexRateLimits? RateLimits(JsonElement snapshot) => snapshot.ValueKind != JsonValueKind.Object
        ? null
        : new CodexRateLimits(Window(CodexJson.Obj(snapshot, "primary")), Window(CodexJson.Obj(snapshot, "secondary")), CodexJson.Str(snapshot, "planType"));

    private static CodexLimitWindow? Window(JsonElement w) => CodexJson.Double(w, "usedPercent") is { } used
        ? new CodexLimitWindow(used, (int?)CodexJson.Long(w, "windowDurationMins"), Time(CodexJson.Long(w, "resetsAt")))
        : null;

    /// <summary>Its name, else its first prompt (one line, shortened).</summary>
    internal static string Title(JsonElement thread)
    {
        if (CodexJson.Str(thread, "name") is { Length: > 0 } name)
        {
            return name.Trim();
        }

        var preview = (CodexJson.Str(thread, "preview") ?? "").ReplaceLineEndings(" ").Trim();
        return preview.Length > 80 ? preview[..77].TrimEnd() + "…" : preview;
    }

    internal static DateTime? Time(long? unixSeconds) => unixSeconds is { } s and > 0 ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;
}
