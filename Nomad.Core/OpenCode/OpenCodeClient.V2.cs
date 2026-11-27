using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Protocol;

namespace Nomad.Core.OpenCode;

/// <summary>HTTP operations for OpenCode's v2 server contract.</summary>
public static partial class OpenCodeClient
{
    /// <summary>OAuth attempts belong to the server that created them.</summary>
    private static readonly ConditionalWeakTable<OpenCodeServer, System.Collections.Concurrent.ConcurrentDictionary<(string, int), string>>
        OAuthAttempts = new();

    /// <summary>Reads an OpenCode v2 response envelope.</summary>
    private static JsonElement Data(JsonDocument doc) => doc.RootElement.GetProperty("data");

    /// <summary>Builds a session endpoint for the running API version.</summary>
    private static string SessionUrl(OpenCodeServer server, string sessionId, string? directory, string? action = null)
    {
        var path = $"session/{Uri.EscapeDataString(sessionId)}";
        if (action is not null) path += "/" + action;

        return server.IsV2 ? "api/" + path : Url(path, directory);
    }

    /// <summary>Translates permission rules to v2's action, resource and effect fields.</summary>
    private static JsonArray V2Rules(JsonArray permission) => new([.. permission.Select(rule => (JsonNode)new JsonObject
    {
        ["action"] = rule?["permission"]?.GetValue<string>() is "bash" ? "shell" : rule?["permission"]?.DeepClone(),
        ["resource"] = rule?["pattern"]?.DeepClone(),
        ["effect"] = rule?["action"]?.DeepClone(),
    })]);

    /// <summary>Creates a v2 session in the selected folder with Nomad's permission rules.</summary>
    private static async Task<string> CreateV2SessionAsync(OpenCodeServer server, string directory, JsonArray permission, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["location"] = new JsonObject { ["directory"] = directory },
            ["permissions"] = V2Rules(permission),
        };

        using var doc = await SendAsync(server, HttpMethod.Post, "api/session", body, ct).ConfigureAwait(false);
        return Data(doc).GetProperty("id").GetString()!;
    }

    /// <summary>Selects the agent and model on the session before queuing the prompt.</summary>
    private static async Task PromptV2Async(
        OpenCodeServer server, string sessionId, string text, IReadOnlyList<PromptImage>? images, string? model, string? agent,
        string? messageId, string? variant, string? system, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(agent))
        {
            using var _ = await SendAsync(server, HttpMethod.Post, SessionUrl(server, sessionId, null, "agent"),
                new JsonObject { ["agent"] = agent }, ct).ConfigureAwait(false);
        }

        if (SplitModel(model) is var (provider, id))
        {
            var selection = new JsonObject { ["providerID"] = provider, ["id"] = id };
            if (!string.IsNullOrEmpty(variant)) selection["variant"] = variant;

            using var _ = await SendAsync(server, HttpMethod.Post, SessionUrl(server, sessionId, null, "model"),
                new JsonObject { ["model"] = selection }, ct).ConfigureAwait(false);
        }

        var body = new JsonObject { ["text"] = system is null ? text : system + "\n\n" + text };
        if (messageId is not null) body["id"] = messageId;
        if (images is { Count: > 0 })
        {
            body["files"] = new JsonArray([.. images.Select(image => (JsonNode)new JsonObject
            {
                ["uri"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}",
                ["source"] = new JsonObject { ["type"] = "inline" },
            })]);
        }

        using var prompt = await SendAsync(server, HttpMethod.Post, SessionUrl(server, sessionId, null, "prompt"), body, ct).ConfigureAwait(false);
    }

    /// <summary>Reads v2 session pages up to the requested limit.</summary>
    private static async Task<IReadOnlyList<OpenCodeSessionInfo>> ListV2SessionsAsync(
        OpenCodeServer server, int limit, bool rootsOnly, CancellationToken ct)
    {
        var sessions = new List<OpenCodeSessionInfo>();
        string? cursor = null;
        while (sessions.Count < limit)
        {
            var url = $"api/session?limit={Math.Min(limit - sessions.Count, 100)}";
            if (cursor is not null) url += "&cursor=" + Uri.EscapeDataString(cursor);

            using var doc = await SendAsync(server, HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            var page = Data(doc);
            AddSessions(sessions, page, rootsOnly, limit);

            var next = OpenCodeJson.Str(OpenCodeJson.Obj(doc.RootElement, "cursor"), "next");
            if (page.GetArrayLength() == 0 || next is null || next == cursor) break;

            cursor = next;
        }

        return sessions;
    }

    /// <summary>Exports a session and its messages for Hub.</summary>
    internal static async Task<string> ExportV2SessionAsync(OpenCodeServer server, string sessionId, CancellationToken ct)
    {
        using var doc = await SendAsync(server, HttpMethod.Get, $"api/experimental/session/{Uri.EscapeDataString(sessionId)}/export",
            null, ct).ConfigureAwait(false);
        return Data(doc).GetRawText();
    }

    /// <summary>Imports a Hub session into the local checkout.</summary>
    internal static async Task ImportV2SessionAsync(OpenCodeServer server, string json, string? directory, CancellationToken ct)
    {
        var body = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("The OpenCode export is empty.");
        if (directory is not null) body["location"] = new JsonObject { ["directory"] = directory };

        using var _ = await SendAsync(server, HttpMethod.Post, "api/experimental/session/import", body, ct).ConfigureAwait(false);
    }

    /// <summary>Reads all pages in chronological order and converts them to the shared history format.</summary>
    private static async Task<JsonDocument> GetV2MessagesAsync(OpenCodeServer server, string sessionId, CancellationToken ct)
    {
        var messages = new JsonArray();
        var route = SessionUrl(server, sessionId, null, "message") + "?order=asc&limit=100";
        string? cursor = null;

        do
        {
            var url = cursor is null ? route : SessionUrl(server, sessionId, null, "message") + "?cursor=" + Uri.EscapeDataString(cursor);
            using var doc = await SendAsync(server, HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            foreach (var message in Data(doc).EnumerateArray())
            {
                if (V2HistoryMessage(message) is { } item) messages.Add((JsonNode)item);
            }

            var next = OpenCodeJson.Str(OpenCodeJson.Obj(doc.RootElement, "cursor"), "next");
            if (next == cursor) break;

            cursor = next;
        }
        while (cursor is not null);

        return JsonDocument.Parse(messages.ToJsonString());
    }

    /// <summary>Converts a v2 user or assistant message into the shared history format.</summary>
    private static JsonObject? V2HistoryMessage(JsonElement message)
    {
        var role = OpenCodeJson.Str(message, "type");
        if (role is not ("user" or "assistant")) return null;

        var info = JsonObject.Create(message.Clone())!;
        info["role"] = role;
        var model = OpenCodeJson.Obj(message, "model");
        info["providerID"] = OpenCodeJson.Str(model, "providerID");
        info["modelID"] = OpenCodeJson.Str(model, "id");

        var parts = new JsonArray();
        if (role == "user")
        {
            parts.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = OpenCodeJson.Str(message, "text") });
            if (OpenCodeJson.Arr(message, "files") is { } files)
            {
                foreach (var file in files.EnumerateArray())
                {
                    parts.Add((JsonNode)new JsonObject { ["type"] = "file", ["url"] = OpenCodeJson.Str(file, "uri") });
                }
            }
        }
        else if (OpenCodeJson.Arr(message, "content") is { } content)
        {
            foreach (var part in content.EnumerateArray())
            {
                parts.Add((JsonNode)V2HistoryPart(part));
            }
        }

        return new JsonObject { ["info"] = info, ["parts"] = parts };
    }

    /// <summary>Converts stored tool state to the shared tool call and result format.</summary>
    private static JsonObject V2HistoryPart(JsonElement part)
    {
        var node = JsonObject.Create(part.Clone())!;
        if (OpenCodeJson.Str(part, "type") != "tool") return node;

        node["callID"] = OpenCodeJson.Str(part, "id");
        node["tool"] = OpenCodeJson.Str(part, "name");
        var state = OpenCodeJson.Obj(part, "state");
        if (node["state"] is JsonObject toolState)
        {
            toolState["output"] = V2ToolOutput(state);
            if (OpenCodeJson.Obj(state, "error") is { ValueKind: JsonValueKind.Object } error)
            {
                toolState["error"] = OpenCodeJson.Str(error, "message");
            }
        }

        return node;
    }

    /// <summary>Joins the text entries of a v2 tool result.</summary>
    internal static string V2ToolOutput(JsonElement state) => OpenCodeJson.Arr(state, "content") is { } content
        ? string.Join("\n", content.EnumerateArray().Select(part => OpenCodeJson.Str(part, "text")).OfType<string>())
        : "";

    /// <summary>Gives OpenCode's asynchronously loaded plugin catalogs a chance to populate.</summary>
    private static async Task<JsonDocument> ReadV2CatalogAsync(OpenCodeServer server, string route, CancellationToken ct, Func<JsonElement, bool>? ready = null)
    {
        for (var retry = 0; ; retry++)
        {
            var doc = await SendAsync(server, HttpMethod.Get, route, null, ct).ConfigureAwait(false);
            if (retry == 12 || OpenCodeJson.Arr(doc.RootElement, "data") is { } items
                && (ready?.Invoke(items) ?? items.GetArrayLength() > 0))
            {
                return doc;
            }

            doc.Dispose();
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the v2 chat model catalog and its configured default.</summary>
    private static async Task<(IReadOnlyList<OpenCodeModel>, string?)> ListV2ModelsAsync(OpenCodeServer server, CancellationToken ct)
    {
        using var doc = await ReadV2CatalogAsync(server, "api/model?types=chat", ct).ConfigureAwait(false);
        using var defaults = await SendAsync(server, HttpMethod.Get, "api/model/default", null, ct).ConfigureAwait(false);

        var models = new List<OpenCodeModel>();
        foreach (var model in Data(doc).EnumerateArray())
        {
            if (OpenCodeJson.Str(model, "providerID") is not { } provider || OpenCodeJson.Str(model, "id") is not { } id
                || OpenCodeJson.Str(model, "status") == "deprecated" || model.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            var context = OpenCodeJson.Long(OpenCodeJson.Obj(model, "limit"), "context") ?? 0;
            var variants = OpenCodeJson.Arr(model, "variants")?.EnumerateArray().Select(v => OpenCodeJson.Str(v, "id")).OfType<string>().ToList() ?? [];
            var caps = OpenCodeJson.Obj(model, "capabilities");
            var reasoning = variants.Count > 0 || caps.ValueKind == JsonValueKind.Object
                && caps.TryGetProperty("reasoning", out var r) && r.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
            models.Add(new OpenCodeModel(provider, provider, id, OpenCodeJson.Str(model, "name") ?? id, (int)Math.Min(context, int.MaxValue), reasoning, variants));
        }

        var selected = Data(defaults);
        var fallback = OpenCodeJson.Str(selected, "providerID") is { } p && OpenCodeJson.Str(selected, "id") is { } m ? $"{p}/{m}" : null;
        return (models, fallback);
    }

    /// <summary>Loads browser sign-in methods and active credentials from OpenCode v2 integrations.</summary>
    private static async Task<(IReadOnlyList<OpenCodeOAuthProvider>, IReadOnlyList<OpenCodeConnectedProvider>)> GetV2ProviderAuthAsync(
        OpenCodeServer server, CancellationToken ct)
    {
        using var doc = await ReadV2CatalogAsync(server, "api/integration", ct, items => items.EnumerateArray().Any(integration =>
            OpenCodeJson.Arr(integration, "methods") is { } methods
            && methods.EnumerateArray().Any(method => OpenCodeJson.Str(method, "type") == "oauth"))).ConfigureAwait(false);
        var providers = new List<OpenCodeOAuthProvider>();
        var connected = new List<OpenCodeConnectedProvider>();

        foreach (var integration in Data(doc).EnumerateArray())
        {
            if (OpenCodeJson.Str(integration, "id") is not { } id) continue;

            var name = OpenCodeJson.Str(integration, "name") ?? id;
            if (OpenCodeJson.Arr(integration, "methods") is { } methods)
            {
                for (var index = 0; index < methods.GetArrayLength(); index++)
                {
                    var method = methods[index];
                    if (OpenCodeJson.Str(method, "type") != "oauth") continue;

                    providers.Add(new OpenCodeOAuthProvider(id, name, index, OpenCodeJson.Str(method, "label") ?? name,
                        HasV2OAuthConnection(integration), V2AuthPrompts(method)));
                }
            }

            if (OpenCodeJson.Arr(integration, "connections") is not { } connections) continue;

            var active = connections.EnumerateArray().Where(c => OpenCodeJson.Obj(c, "status").ValueKind != JsonValueKind.Object).ToArray();
            if (active.Length > 0)
            {
                connected.Add(new OpenCodeConnectedProvider(id, name, active.Any(c => OpenCodeJson.Str(c, "method") == "oauth")));
            }
        }

        return (providers, connected);
    }

    /// <summary>Whether the integration has an authenticated OAuth connection.</summary>
    private static bool HasV2OAuthConnection(JsonElement integration) =>
        OpenCodeJson.Arr(integration, "connections") is { } connections
        && connections.EnumerateArray().Any(c => OpenCodeJson.Str(c, "method") == "oauth"
            && OpenCodeJson.Obj(c, "status").ValueKind != JsonValueKind.Object);

    /// <summary>Maps the input fields from an integration's OAuth form.</summary>
    private static IReadOnlyList<OpenCodeAuthPrompt> V2AuthPrompts(JsonElement method)
    {
        if (OpenCodeJson.Arr(method, "form") is not { } fields) return [];

        var prompts = new List<OpenCodeAuthPrompt>();
        foreach (var field in fields.EnumerateArray())
        {
            if (OpenCodeJson.Str(field, "key") is not { } key) continue;

            var options = OpenCodeJson.Arr(field, "options")?.EnumerateArray()
                .Select(o => (Label: OpenCodeJson.Str(o, "label"), Value: OpenCodeJson.Str(o, "value")))
                .Where(o => o.Value is not null)
                .Select(o => (o.Label ?? o.Value!, o.Value!))
                .ToList();

            prompts.Add(new OpenCodeAuthPrompt(key, OpenCodeJson.Str(field, "title") ?? key, OpenCodeJson.Str(field, "placeholder"),
                OpenCodeJson.Str(field, "default"), options is { Count: > 0 }, options));
        }

        return prompts;
    }

    /// <summary>Creates an OAuth attempt using the identifier declared by the integration.</summary>
    private static async Task<OpenCodeOAuthAuthorization> StartV2OAuthAsync(
        OpenCodeServer server, string providerId, int methodIndex, IReadOnlyDictionary<string, string>? inputs, CancellationToken ct)
    {
        var route = $"api/integration/{Uri.EscapeDataString(providerId)}";
        using var integration = await SendAsync(server, HttpMethod.Get, route, null, ct).ConfigureAwait(false);
        var method = Data(integration).GetProperty("methods")[methodIndex];
        var body = new JsonObject { ["methodID"] = method.GetProperty("id").GetString() };
        if (inputs is { Count: > 0 })
        {
            var answers = new JsonObject();
            foreach (var (key, value) in inputs) answers[key] = value;

            body["answer"] = answers;
        }

        using var doc = await SendAsync(server, HttpMethod.Post, route + "/connect/oauth", body, ct).ConfigureAwait(false);
        var attempt = Data(doc);
        OAuthAttempts.GetOrCreateValue(server)[(providerId, methodIndex)] = attempt.GetProperty("attemptID").GetString()!;
        return new OpenCodeOAuthAuthorization(attempt.GetProperty("url").GetString()!,
            OpenCodeJson.Str(attempt, "mode") ?? "auto", OpenCodeJson.Str(attempt, "instructions") ?? "");
    }

    /// <summary>Submits an authorization code or waits for browser approval.</summary>
    private static async Task CompleteV2OAuthAsync(OpenCodeServer server, string providerId, int index, string? code, CancellationToken ct)
    {
        var attempts = OAuthAttempts.GetOrCreateValue(server);
        if (!attempts.TryRemove((providerId, index), out var attempt)) throw new InvalidOperationException("OpenCode sign-in has expired.");

        var route = $"api/integration/{Uri.EscapeDataString(providerId)}/connect/oauth/{Uri.EscapeDataString(attempt)}";
        if (!string.IsNullOrWhiteSpace(code))
        {
            using var _ = await SendAsync(server, HttpMethod.Post, route + "/complete", new JsonObject { ["code"] = code.Trim() }, ct)
                .ConfigureAwait(false);
            return;
        }

        while (true)
        {
            using var doc = await SendAsync(server, HttpMethod.Get, route, null, ct).ConfigureAwait(false);
            var state = Data(doc);
            switch (OpenCodeJson.Str(state, "status"))
            {
                case "pending":
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    break;
                case "complete":
                    return;
                default:
                    throw new InvalidOperationException(OpenCodeJson.Str(state, "message") ?? "OpenCode sign-in failed.");
            }
        }
    }

    /// <summary>Removes the credentials owned by the selected v2 integration.</summary>
    private static async Task RemoveV2AuthAsync(OpenCodeServer server, string providerId, CancellationToken ct)
    {
        using var doc = await SendAsync(server, HttpMethod.Get, $"api/integration/{Uri.EscapeDataString(providerId)}", null, ct).ConfigureAwait(false);
        if (OpenCodeJson.Arr(Data(doc), "connections") is not { } connections) return;

        foreach (var connection in connections.EnumerateArray())
        {
            if (OpenCodeJson.Str(connection, "type") != "credential" || OpenCodeJson.Str(connection, "id") is not { } id) continue;

            using var _ = await SendAsync(server, HttpMethod.Delete, $"api/credential/{Uri.EscapeDataString(id)}", null, ct).ConfigureAwait(false);
        }
    }
    /// <summary>Answers or dismisses a v2 session form.</summary>
    internal static async Task ReplyV2FormAsync(OpenCodeServer server, string sessionId, string formId,
        IReadOnlyList<IReadOnlyList<string>>? answers, CancellationToken ct)
    {
        var route = SessionUrl(server, sessionId, null, $"form/{Uri.EscapeDataString(formId)}");
        if (answers is null)
        {
            using var _ = await SendAsync(server, HttpMethod.Delete, route, null, ct).ConfigureAwait(false);
            return;
        }

        using var form = await SendAsync(server, HttpMethod.Get, route, null, ct).ConfigureAwait(false);
        var fields = Data(form).GetProperty("fields");
        var answer = new JsonObject();
        for (var index = 0; index < Math.Min(fields.GetArrayLength(), answers.Count); index++)
        {
            var field = fields[index];
            var values = answers[index];
            if (OpenCodeJson.Str(field, "key") is not { } key || values.Count == 0) continue;

            answer[key] = OpenCodeJson.Str(field, "type") switch
            {
                "multiselect" => new JsonArray([.. values.Select(value => (JsonNode?)JsonValue.Create(value))]),
                "boolean" when bool.TryParse(values[0], out var boolean) => JsonValue.Create(boolean),
                "number" or "integer" when double.TryParse(values[0], System.Globalization.CultureInfo.InvariantCulture, out var number) =>
                    JsonValue.Create(number),
                _ => JsonValue.Create(values[0]),
            };
        }

        using var reply = await SendAsync(server, HttpMethod.Post, route + "/reply", new JsonObject { ["answer"] = answer }, ct).ConfigureAwait(false);
    }

}
