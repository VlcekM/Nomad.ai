using System.Text.Json;
using Nomad.Core.Sessions;

namespace Nomad.Core.OpenCode;

/// <summary>An OpenCode session's stored messages as Nomad's <see cref="SessionHistory"/>.</summary>
public static class OpenCodeHistory
{
    /// <summary>
    /// Builds the history from <c>GET /session/{id}/message</c> (an array of <c>{ info, parts }</c>). A prompt's
    /// <see cref="HistoryUserMessage.Uuid"/> and <see cref="HistoryUserMessage.ParentUuid"/> are both its own message
    /// id: rewinding or forking "at" it means just before it in OpenCode (revert, fork).
    /// </summary>
    public static SessionHistory FromMessages(JsonElement messages, string sessionId, string? fallbackDirectory)
    {
        var items = new List<HistoryItem>();
        string? model = null;
        string? cwd = null;
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return new SessionHistory { SessionId = sessionId, WorkingDirectory = fallbackDirectory, Items = items };
        }

        foreach (var message in messages.EnumerateArray())
        {
            var info = OpenCodeJson.Obj(message, "info");
            var id = OpenCodeJson.Str(info, "id") ?? "";
            var parts = OpenCodeJson.Arr(message, "parts");
            if (OpenCodeJson.Str(info, "role") == "user")
            {
                var text = new List<string>();
                var images = new List<HistoryImage>();
                foreach (var part in (parts ?? Empty).EnumerateArray())
                {
                    switch (OpenCodeJson.Str(part, "type"))
                    {
                        case "text" when !IsTrue(part, "synthetic") && OpenCodeJson.Str(part, "text") is { Length: > 0 } t:
                            text.Add(t);
                            break;
                        case "file" when OpenCodeJson.Str(part, "url") is { } url && DataUrl(url) is { } image:
                            images.Add(image);
                            break;
                    }
                }

                if (text.Count > 0 || images.Count > 0)
                {
                    var created = OpenCodeJson.Long(OpenCodeJson.Obj(info, "time"), "created");
                    items.Add(new HistoryUserMessage(string.Join("\n", text))
                    {
                        Uuid = id,
                        ParentUuid = id,
                        TimestampUtc = created is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null,
                        Images = images,
                    });
                }

                continue;
            }

            if (OpenCodeJson.Str(info, "providerID") is { } provider && OpenCodeJson.Str(info, "modelID") is { } modelId)
            {
                model = $"{provider}/{modelId}";
            }

            cwd ??= OpenCodeJson.Str(OpenCodeJson.Obj(info, "path"), "cwd");
            foreach (var part in (parts ?? Empty).EnumerateArray())
            {
                switch (OpenCodeJson.Str(part, "type"))
                {
                    case "text" when OpenCodeJson.Str(part, "text") is { Length: > 0 } t:
                        items.Add(new HistoryAssistantText(id, t));
                        break;
                    case "reasoning" when OpenCodeJson.Str(part, "text") is { Length: > 0 } t:
                        items.Add(new HistoryThinking(t));
                        break;
                    case "tool" when OpenCodeJson.Str(part, "callID") is { } callId:
                        var state = OpenCodeJson.Obj(part, "state");
                        var (name, input) = OpenCodeTools.Map(OpenCodeJson.Str(part, "tool") ?? "tool", OpenCodeJson.Obj(state, "input"), cwd ?? fallbackDirectory);
                        items.Add(new HistoryToolCall(callId, name, input));
                        switch (OpenCodeJson.Str(state, "status"))
                        {
                            case "completed":
                                items.Add(new HistoryToolResult(callId, false, OpenCodeJson.Str(state, "output") ?? "", OpenCodeTools.Structured(OpenCodeJson.Obj(state, "metadata"))));
                                break;
                            case "error":
                                items.Add(new HistoryToolResult(callId, true, OpenCodeJson.Str(state, "error") ?? "The tool failed.", default));
                                break;
                        }

                        break;
                }
            }

            if (OpenCodeJson.Obj(info, "error") is { ValueKind: JsonValueKind.Object } error)
            {
                items.Add(new HistoryNotice(OpenCodeJson.Str(error, "name") == "MessageAbortedError"
                    ? "Interrupted"
                    : OpenCodeTranslator.ErrorText(error) ?? "Error"));
            }
        }

        return new SessionHistory { SessionId = sessionId, WorkingDirectory = cwd ?? fallbackDirectory, Model = model, Items = items };
    }

    private static readonly JsonElement Empty = JsonDocument.Parse("[]").RootElement.Clone();

    private static bool IsTrue(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static HistoryImage? DataUrl(string url)
    {
        // data:image/png;base64,....
        if (!url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = url.IndexOf(',');
        var semicolon = url.IndexOf(';');
        return comma > 0 && semicolon > 5 && semicolon < comma && url[semicolon..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)
            ? new HistoryImage(url[5..semicolon], url[(comma + 1)..])
            : null;
    }
}
