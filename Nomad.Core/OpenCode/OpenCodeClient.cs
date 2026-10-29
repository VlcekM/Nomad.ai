using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Protocol;

namespace Nomad.Core.OpenCode;

/// <summary>A model OpenCode can run: one of a connected provider's (OpenRouter, Anthropic, OpenCode Zen, …).</summary>
public sealed record OpenCodeModel(string ProviderId, string ProviderName, string ModelId, string Name, int ContextWindow, bool Reasoning)
{
    /// <summary>"provider/model", the id Nomad keeps for an OpenCode session's model.</summary>
    public string Id => $"{ProviderId}/{ModelId}";
}

/// <summary>A primary OpenCode agent the user can switch to (build, plan, custom ones).</summary>
public sealed record OpenCodeAgent(string Name, string? Description);

/// <summary>A stored OpenCode session, for the session list.</summary>
public sealed record OpenCodeSessionInfo(string Id, string? Directory, string Title, DateTime UpdatedUtc, string? Model);

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
        string? model, string? agent, string? messageId, CancellationToken ct = default)
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
                    models.Add(new OpenCodeModel(providerId, providerName, OpenCodeJson.Str(m, "id") ?? entry.Name, OpenCodeJson.Str(m, "name") ?? entry.Name, (int)Math.Min(context, int.MaxValue), reasoning));
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
