using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Protocol;

namespace Nomad.Core.OpenCode;

/// <summary>
/// A model OpenCode can run: one of a connected provider's (OpenRouter, Anthropic, OpenCode Zen, …). Its variants are
/// its effort levels as OpenCode names them ("low", "high", "max", …), lowest first; none when it has no such choice.
/// </summary>
public sealed record OpenCodeModel(string ProviderId, string ProviderName, string ModelId, string Name, int ContextWindow, bool Reasoning, IReadOnlyList<string> Variants)
{
    /// <summary>"provider/model", the id Nomad keeps for an OpenCode session's model.</summary>
    public string Id => $"{ProviderId}/{ModelId}";
}

/// <summary>A primary OpenCode agent the user can switch to (build, plan, custom ones).</summary>
public sealed record OpenCodeAgent(string Name, string? Description);

/// <summary>A stored OpenCode session, for the session list.</summary>
public sealed record OpenCodeSessionInfo(string Id, string? Directory, string Title, DateTime UpdatedUtc, string? Model);

/// <summary>An input prompt an OpenCode OAuth method asks for before opening the browser (e.g. GitLab instance URL).</summary>
public sealed record OpenCodeAuthPrompt(
    string Key,
    string Label,
    string? Placeholder,
    string? DefaultValue,
    bool IsSelect,
    IReadOnlyList<(string Label, string Value)>? Options,
    string? ConditionKey = null,
    string? ConditionValue = null);

/// <summary>An OAuth subscription login method offered by an OpenCode provider.</summary>
public sealed record OpenCodeOAuthProvider(
    string Id,
    string Name,
    int MethodIndex,
    string MethodLabel,
    bool IsConnected,
    IReadOnlyList<OpenCodeAuthPrompt> Prompts);

/// <summary>A connected OpenCode provider (OAuth subscription or API key).</summary>
public sealed record OpenCodeConnectedProvider(string Id, string Name, bool IsOAuth);

/// <summary>What <c>POST /provider/{id}/oauth/authorize</c> returns: the browser URL, callback mode ("auto" | "code"), and instructions.</summary>
public sealed record OpenCodeOAuthAuthorization(string Url, string Method, string Instructions);

/// <summary>The calls Nomad makes to <c>opencode serve</c> (its v1 API, the one OpenCode's own TUI uses).</summary>
public static class OpenCodeClient
{
    private static string Q(string? directory) => directory is null ? "" : "directory=" + Uri.EscapeDataString(directory);

    private static string Url(string path, string? directory, string? extra = null)
    {
        var query = string.Join('&', new[] { Q(directory), extra }.Where(s => !string.IsNullOrEmpty(s)));
        return query.Length == 0 ? path : $"{path}?{query}";
    }

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> SendAsync(OpenCodeServer server, HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : Json(body) };
        using var response = await server.Http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OpenCode: {method} {url.Split('?')[0]} failed ({(int)response.StatusCode}): {ErrorText(text)}");
        }

        return JsonDocument.Parse(text.Length == 0 ? "null" : text);
    }

    private static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return OpenCodeJson.Str(OpenCodeJson.Obj(doc.RootElement, "data"), "message") ?? OpenCodeJson.Str(doc.RootElement, "name") ?? body;
        }
        catch (JsonException)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }

    /// <summary>A new session in <paramref name="directory"/> with these permission rules; its id.</summary>
    public static async Task<string> CreateSessionAsync(OpenCodeServer server, string directory, JsonArray permission, CancellationToken ct = default)
    {
        using var doc = await SendAsync(server, HttpMethod.Post, Url("session", directory), new JsonObject { ["permission"] = permission }, ct).ConfigureAwait(false);
        return OpenCodeJson.Str(doc.RootElement, "id") ?? throw new InvalidOperationException("OpenCode created a session without an id.");
    }

    /// <summary>A copy of the session up to (not including) <paramref name="messageId"/>, or all of it; its id.</summary>
    public static async Task<string> ForkSessionAsync(OpenCodeServer server, string directory, string sessionId, string? messageId, CancellationToken ct = default)
    {
        var body = new JsonObject();
        if (messageId is not null)
        {
            body["messageID"] = messageId;
        }

        using var doc = await SendAsync(server, HttpMethod.Post, Url($"session/{sessionId}/fork", directory), body, ct).ConfigureAwait(false);
        return OpenCodeJson.Str(doc.RootElement, "id") ?? throw new InvalidOperationException("OpenCode forked a session without an id.");
    }

    /// <summary>Changes a session's title and/or permission rules.</summary>
    public static async Task UpdateSessionAsync(OpenCodeServer server, string directory, string sessionId, string? title, JsonArray? permission, CancellationToken ct = default)
    {
        var body = new JsonObject();
        if (title is not null)
        {
            body["title"] = title;
        }

        if (permission is not null)
        {
            body["permission"] = permission;
        }

        using var _ = await SendAsync(server, HttpMethod.Patch, Url($"session/{sessionId}", directory), body, ct).ConfigureAwait(false);
    }

    /// <summary>Sends a prompt; the turn runs in the background and reports over the event stream.</summary>
    public static async Task PromptAsync(
        OpenCodeServer server, string directory, string sessionId, string text, IReadOnlyList<PromptImage>? images,
        string? model, string? agent, string? messageId, string? variant = null, CancellationToken ct = default, string? system = null)
    {
        var parts = new JsonArray((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in images ?? [])
        {
            parts.Add((JsonNode)new JsonObject
            {
                ["type"] = "file",
                ["mime"] = image.MediaType,
                ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}",
            });
        }

        var body = new JsonObject { ["parts"] = parts };
        if (system is not null) body["system"] = system;
        if (messageId is not null)
        {
            body["messageID"] = messageId;
        }

        if (SplitModel(model) is var (provider, id))
        {
            body["model"] = new JsonObject { ["providerID"] = provider, ["modelID"] = id };
        }

        if (!string.IsNullOrEmpty(agent))
        {
            body["agent"] = agent;
        }

        // The model's effort level (one of its variants); without one, OpenCode uses the model's default.
        if (!string.IsNullOrEmpty(variant))
        {
            body["variant"] = variant;
        }

        using var _ = await SendAsync(server, HttpMethod.Post, Url($"session/{sessionId}/prompt_async", directory), body, ct).ConfigureAwait(false);
    }

    /// <summary>Rewinds the session to just before <paramref name="messageId"/>: it and everything after go with the next prompt.</summary>
    public static async Task RevertAsync(OpenCodeServer server, string directory, string sessionId, string messageId, CancellationToken ct = default)
    {
        using var _ = await SendAsync(server, HttpMethod.Post, Url($"session/{sessionId}/revert", directory), new JsonObject { ["messageID"] = messageId }, ct).ConfigureAwait(false);
    }

    public static async Task DeleteSessionAsync(OpenCodeServer server, string? directory, string sessionId, CancellationToken ct = default)
    {
        using var _ = await SendAsync(server, HttpMethod.Delete, Url($"session/{sessionId}", directory), null, ct).ConfigureAwait(false);
    }

    public static async Task AbortAsync(OpenCodeServer server, string directory, string sessionId, CancellationToken ct = default)
    {
        using var _ = await SendAsync(server, HttpMethod.Post, Url($"session/{sessionId}/abort", directory), null, ct).ConfigureAwait(false);
    }

    /// <summary>Answers a permission request: once | always | reject.</summary>
    public static async Task ReplyPermissionAsync(OpenCodeServer server, string directory, string requestId, string reply, string? message, CancellationToken ct = default)
    {
        var body = new JsonObject { ["reply"] = reply };
        if (!string.IsNullOrEmpty(message))
        {
            body["message"] = message;
        }

        using var _ = await SendAsync(server, HttpMethod.Post, Url($"permission/{requestId}/reply", directory), body, ct).ConfigureAwait(false);
    }

    /// <summary>Answers a question request: for each question in order, the labels chosen (or the typed answer).</summary>
    public static async Task ReplyQuestionAsync(OpenCodeServer server, string directory, string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct = default)
    {
        var array = new JsonArray();
        foreach (var answer in answers)
        {
            array.Add((JsonNode)new JsonArray([.. answer.Select(a => (JsonNode?)JsonValue.Create(a))]));
        }

        using var _ = await SendAsync(server, HttpMethod.Post, Url($"question/{requestId}/reply", directory), new JsonObject { ["answers"] = array }, ct).ConfigureAwait(false);
    }

    public static async Task RejectQuestionAsync(OpenCodeServer server, string directory, string requestId, CancellationToken ct = default)
    {
        using var _ = await SendAsync(server, HttpMethod.Post, Url($"question/{requestId}/reject", directory), null, ct).ConfigureAwait(false);
    }

    /// <summary>A session's stored messages (each <c>{ info, parts }</c>), oldest first.</summary>
    public static Task<JsonDocument> GetMessagesAsync(OpenCodeServer server, string? directory, string sessionId, CancellationToken ct = default) =>
        SendAsync(server, HttpMethod.Get, Url($"session/{sessionId}/message", directory), null, ct);

    /// <summary>Asks OpenCode to write a short title for the session (it also does so by itself after the first prompt).</summary>
    public static async Task<string?> GetTitleAsync(OpenCodeServer server, string? directory, string sessionId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(server, HttpMethod.Get, Url($"session/{sessionId}", directory), null, ct).ConfigureAwait(false);
        return OpenCodeJson.Str(doc.RootElement, "title");
    }

    /// <summary>Every top-level session OpenCode has, in every project, newest first.</summary>
    public static async Task<IReadOnlyList<OpenCodeSessionInfo>> ListSessionsAsync(OpenCodeServer server, int limit = 500, CancellationToken ct = default)
    {
        using var doc = await SendAsync(server, HttpMethod.Get, Url("experimental/session", null, $"roots=true&limit={limit}"), null, ct).ConfigureAwait(false);
        var list = new List<OpenCodeSessionInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var s in doc.RootElement.EnumerateArray())
        {
            if (OpenCodeJson.Str(s, "id") is not { } id || OpenCodeJson.Str(s, "parentID") is not null)
            {
                continue;
            }

            var time = OpenCodeJson.Obj(s, "time");
            var updated = OpenCodeJson.Long(time, "updated") ?? OpenCodeJson.Long(time, "created") ?? 0;
            var model = OpenCodeJson.Obj(s, "model");
            var modelId = OpenCodeJson.Str(model, "providerID") is { } p && OpenCodeJson.Str(model, "id") is { } m ? $"{p}/{m}" : null;
            list.Add(new OpenCodeSessionInfo(
                id,
                OpenCodeJson.Str(s, "directory"),
                OpenCodeTitles.Clean(OpenCodeJson.Str(s, "title")),
                DateTimeOffset.FromUnixTimeMilliseconds(updated).UtcDateTime,
                modelId));
        }

        return list;
    }

    /// <summary>The models of every connected provider, and the default model ("provider/model") if OpenCode names one.</summary>
    public static async Task<(IReadOnlyList<OpenCodeModel> Models, string? Default)> ListModelsAsync(OpenCodeServer server, CancellationToken ct = default)
    {
        using var doc = await SendAsync(server, HttpMethod.Get, "config/providers", null, ct).ConfigureAwait(false);
        var models = new List<OpenCodeModel>();
        string? fallback = null;
        if (OpenCodeJson.Arr(doc.RootElement, "providers") is { } providers)
        {
            foreach (var p in providers.EnumerateArray())
            {
                var providerId = OpenCodeJson.Str(p, "id") ?? "";
                var providerName = OpenCodeJson.Str(p, "name") ?? providerId;
                if (!p.TryGetProperty("models", out var ms) || ms.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var entry in ms.EnumerateObject())
                {
                    var m = entry.Value;
                    if (OpenCodeJson.Str(m, "status") is "deprecated" or "disabled")
                    {
                        continue;
                    }

                    var context = OpenCodeJson.Long(OpenCodeJson.Obj(m, "limit"), "context") ?? 0;
                    var reasoning = OpenCodeJson.Obj(m, "capabilities") is { ValueKind: JsonValueKind.Object } caps
                        && caps.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.True;
                    // Variants come in OpenCode's order, which is lowest effort first.
                    var variants = m.TryGetProperty("variants", out var vs) && vs.ValueKind == JsonValueKind.Object
                        ? vs.EnumerateObject().Select(v => v.Name).ToList()
                        : [];
                    models.Add(new OpenCodeModel(providerId, providerName, OpenCodeJson.Str(m, "id") ?? entry.Name, OpenCodeJson.Str(m, "name") ?? entry.Name, (int)Math.Min(context, int.MaxValue), reasoning, variants));
                }
            }
        }

        if (doc.RootElement.TryGetProperty("default", out var defaults) && defaults.ValueKind == JsonValueKind.Object)
        {
            foreach (var d in defaults.EnumerateObject())
            {
                if (d.Value.ValueKind == JsonValueKind.String)
                {
                    fallback ??= $"{d.Name}/{d.Value.GetString()}";
                }
            }
        }

        return (models, fallback);
    }

    /// <summary>The primary agents the user can pick (build, plan, and custom ones), without OpenCode's hidden ones.</summary>
    public static async Task<IReadOnlyList<OpenCodeAgent>> ListAgentsAsync(OpenCodeServer server, CancellationToken ct = default)
    {
        using var doc = await SendAsync(server, HttpMethod.Get, "agent", null, ct).ConfigureAwait(false);
        var list = new List<OpenCodeAgent>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                if (OpenCodeJson.Str(a, "mode") is "primary" or "all" && !(a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True)
                    && OpenCodeJson.Str(a, "name") is { } name)
                {
                    list.Add(new OpenCodeAgent(name, OpenCodeJson.Str(a, "description")));
                }
            }
        }

        return list;
    }

    /// <summary>
    /// All OAuth (browser/subscription) providers OpenCode knows about, plus the currently connected providers.
    /// </summary>
    public static async Task<(IReadOnlyList<OpenCodeOAuthProvider> OAuthProviders, IReadOnlyList<OpenCodeConnectedProvider> Connected)> GetProviderAuthAsync(
        OpenCodeServer server, CancellationToken ct = default)
    {
        using var providersDoc = await SendAsync(server, HttpMethod.Get, "provider", null, ct).ConfigureAwait(false);
        using var authDoc = await SendAsync(server, HttpMethod.Get, "provider/auth", null, ct).ConfigureAwait(false);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (OpenCodeJson.Arr(providersDoc.RootElement, "all") is { } all)
        {
            foreach (var p in all.EnumerateArray())
            {
                if (OpenCodeJson.Str(p, "id") is { Length: > 0 } id)
                {
                    names[id] = OpenCodeJson.Str(p, "name") ?? id;
                }
            }
        }

        var connectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OpenCodeJson.Arr(providersDoc.RootElement, "connected") is { } conn)
        {
            foreach (var item in conn.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id)
                {
                    connectedSet.Add(id);
                }
            }
        }

        var storedTypes = ReadStoredAuthTypes();
        var oauthProviders = new List<OpenCodeOAuthProvider>();
        if (authDoc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in authDoc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                // Prefer the first browser/subscription OAuth method (skipping headless variants when a browser one exists).
                var index = 0;
                int? chosenIndex = null;
                string? chosenLabel = null;
                IReadOnlyList<OpenCodeAuthPrompt> chosenPrompts = [];
                foreach (var method in entry.Value.EnumerateArray())
                {
                    if (string.Equals(OpenCodeJson.Str(method, "type"), "oauth", StringComparison.OrdinalIgnoreCase))
                    {
                        var label = OpenCodeJson.Str(method, "label") ?? "Sign in";
                        var isHeadless = label.Contains("headless", StringComparison.OrdinalIgnoreCase);
                        if (chosenIndex is null || (!isHeadless && chosenLabel!.Contains("headless", StringComparison.OrdinalIgnoreCase)))
                        {
                            chosenIndex = index;
                            chosenLabel = label;
                            chosenPrompts = ParseAuthPrompts(method);
                        }
                    }

                    index++;
                }

                if (chosenIndex is { } idx && chosenLabel is not null)
                {
                    var name = names.GetValueOrDefault(entry.Name) ?? entry.Name;
                    oauthProviders.Add(new OpenCodeOAuthProvider(
                        entry.Name,
                        name,
                        idx,
                        chosenLabel,
                        connectedSet.Contains(entry.Name) || storedTypes.ContainsKey(entry.Name),
                        chosenPrompts));
                }
            }
        }

        // Order familiar subscription providers first, then alphabetically.
        static int Priority(string id) => id.ToLowerInvariant() switch
        {
            "openai" => 0,
            "anthropic" => 1,
            "github-copilot" => 2,
            "xai" => 3,
            "poe" => 4,
            "gitlab" => 5,
            "digitalocean" => 6,
            "snowflake-cortex" => 7,
            _ => 100,
        };
        oauthProviders.Sort((a, b) =>
        {
            var cmp = Priority(a.Id).CompareTo(Priority(b.Id));
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        var connected = new List<OpenCodeConnectedProvider>();
        foreach (var id in storedTypes.Keys.Concat(connectedSet).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // "opencode" is always connected for free Zen models unless explicitly in auth.json.
            if (string.Equals(id, "opencode", StringComparison.OrdinalIgnoreCase) && !storedTypes.ContainsKey(id))
            {
                continue;
            }

            var isOAuth = storedTypes.TryGetValue(id, out var kind) && string.Equals(kind, "oauth", StringComparison.OrdinalIgnoreCase);
            connected.Add(new OpenCodeConnectedProvider(id, names.GetValueOrDefault(id) ?? id, isOAuth));
        }

        return (oauthProviders, connected);
    }

    /// <summary>Starts the OAuth flow for an OpenCode provider; returns the URL to open in a browser.</summary>
    public static async Task<OpenCodeOAuthAuthorization> StartOAuthAsync(
        OpenCodeServer server, string providerId, int methodIndex, IReadOnlyDictionary<string, string>? inputs = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["method"] = methodIndex };
        if (inputs is { Count: > 0 })
        {
            var map = new JsonObject();
            foreach (var (k, v) in inputs)
            {
                map[k] = v;
            }

            body["inputs"] = map;
        }

        using var doc = await SendAsync(server, HttpMethod.Post, $"provider/{Uri.EscapeDataString(providerId)}/oauth/authorize", body, ct).ConfigureAwait(false);
        var url = OpenCodeJson.Str(doc.RootElement, "url")
            ?? throw new InvalidOperationException("OpenCode did not return a sign-in URL.");
        var method = OpenCodeJson.Str(doc.RootElement, "method") ?? "auto";
        var instructions = OpenCodeJson.Str(doc.RootElement, "instructions") ?? "";
        return new OpenCodeOAuthAuthorization(url, method, instructions);
    }

    /// <summary>Completes an OpenCode OAuth flow (blocks until browser callback or device-code approval completes for "auto").</summary>
    public static async Task CompleteOAuthAsync(
        OpenCodeServer server, string providerId, int methodIndex, string? code = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["method"] = methodIndex };
        if (!string.IsNullOrWhiteSpace(code))
        {
            body["code"] = code.Trim();
        }

        using var _ = await SendAsync(server, HttpMethod.Post, $"provider/{Uri.EscapeDataString(providerId)}/oauth/callback", body, ct).ConfigureAwait(false);
    }

    /// <summary>Removes stored credentials for an OpenCode provider.</summary>
    public static async Task RemoveAuthAsync(OpenCodeServer server, string providerId, CancellationToken ct = default)
    {
        using var _ = await SendAsync(server, HttpMethod.Delete, $"auth/{Uri.EscapeDataString(providerId)}", null, ct).ConfigureAwait(false);
    }

    /// <summary>Reads OpenCode's local <c>auth.json</c> (provider id to "oauth" | "api") without starting the server.</summary>
    public static Dictionary<string, string> ReadStoredAuthTypes()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            var path = Path.Combine(data, "opencode", "auth.json");
            if (!File.Exists(path))
            {
                return result;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var type = OpenCodeJson.Str(entry.Value, "type") ?? "api";
                result[entry.Name] = type;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return result;
    }

    private static IReadOnlyList<OpenCodeAuthPrompt> ParseAuthPrompts(JsonElement method)
    {
        if (OpenCodeJson.Arr(method, "prompts") is not { } arr)
        {
            return [];
        }

        var list = new List<OpenCodeAuthPrompt>();
        foreach (var p in arr.EnumerateArray())
        {
            var key = OpenCodeJson.Str(p, "key");
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var type = OpenCodeJson.Str(p, "type") ?? "text";
            var label = OpenCodeJson.Str(p, "message") ?? key;
            var placeholder = OpenCodeJson.Str(p, "placeholder");
            string? defaultValue = null;
            List<(string Label, string Value)>? options = null;
            if (string.Equals(type, "select", StringComparison.OrdinalIgnoreCase) && OpenCodeJson.Arr(p, "options") is { } opts)
            {
                options = [];
                foreach (var o in opts.EnumerateArray())
                {
                    var val = OpenCodeJson.Str(o, "value");
                    if (val is not null)
                    {
                        options.Add((OpenCodeJson.Str(o, "label") ?? val, val));
                    }
                }

                if (options.Count > 0)
                {
                    defaultValue = options[0].Value;
                }
            }

            string? condKey = null;
            string? condVal = null;
            var when = OpenCodeJson.Obj(p, "when");
            if (when.ValueKind == JsonValueKind.Object && OpenCodeJson.Str(when, "op") == "eq")
            {
                condKey = OpenCodeJson.Str(when, "key");
                condVal = OpenCodeJson.Str(when, "value");
            }

            list.Add(new OpenCodeAuthPrompt(key, label, placeholder, defaultValue, options is not null, options, condKey, condVal));
        }

        return list;
    }

    /// <summary>
    /// Reads the server's event stream for <paramref name="directory"/> until cancelled or closed, handing each
    /// event (<c>{ type, properties }</c>) to <paramref name="onEvent"/> on this task's thread.
    /// </summary>
    public static async Task ReadEventsAsync(OpenCodeServer server, string directory, Action<JsonElement> onEvent, Action? onConnected, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("event", directory));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await server.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        onConnected?.Invoke();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    Dispatch(data.ToString(), onEvent);
                    data.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
            }
        }

        static void Dispatch(string json, Action<JsonElement> onEvent)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return;
            }

            using (doc)
            {
                onEvent(doc.RootElement);
            }
        }
    }

    /// <summary>"openrouter/anthropic/claude-sonnet-4.6" → ("openrouter", "anthropic/claude-sonnet-4.6"); null without a provider.</summary>
    public static (string Provider, string Model)? SplitModel(string? id)
    {
        var slash = id?.IndexOf('/') ?? -1;
        return slash > 0 && slash < id!.Length - 1 ? (id[..slash], id[(slash + 1)..]) : null;
    }
}
