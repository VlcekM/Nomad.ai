using System.Text.Json;
using Nomad.Core.Sessions;

namespace Nomad.Core.Codex;

/// <summary>A stored Codex thread as Nomad's <see cref="SessionHistory"/>.</summary>
public static class CodexHistory
{
    /// <summary>
    /// Builds the history from <c>thread/read</c>'s thread (with its turns). A prompt's <see cref="HistoryUserMessage.Uuid"/>
    /// and <see cref="HistoryUserMessage.ParentUuid"/> are both the id Nomad gave it (else its turn's): rewinding or forking
    /// "at" it means just before its turn (<see cref="CodexBackend"/> reverts the thread there).
    /// </summary>
    public static SessionHistory FromThread(JsonElement thread, string threadId, string? fallbackDirectory)
    {
        var items = new List<HistoryItem>();
        var cwd = CodexJson.Str(thread, "cwd") ?? fallbackDirectory;
        foreach (var turn in CodexJson.Arr(thread, "turns"))
        {
            var turnId = CodexJson.Str(turn, "id") ?? "";
            var started = CodexClient.Time(CodexJson.Long(turn, "startedAt"));
            foreach (var item in CodexJson.Arr(turn, "items"))
            {
                var id = CodexJson.Str(item, "id") ?? "";
                switch (CodexJson.Str(item, "type"))
                {
                    case "userMessage":
                    {
                        var (text, images) = UserContent(item);
                        if (text.Length > 0 || images.Count > 0)
                        {
                            var uuid = CodexJson.Str(item, "clientId") ?? turnId;
                            items.Add(new HistoryUserMessage(text) { Uuid = uuid, ParentUuid = uuid, TimestampUtc = started, Images = images });
                        }

                        break;
                    }

                    case "agentMessage" or "plan" when CodexJson.Str(item, "text") is { Length: > 0 } text:
                        items.Add(new HistoryAssistantText(id, text));
                        break;

                    case "reasoning":
                    {
                        var summary = string.Join("\n\n", CodexJson.Strings(item, "summary").Where(s => s.Length > 0));
                        if (summary.Length > 0)
                        {
                            items.Add(new HistoryThinking(summary));
                        }

                        break;
                    }

                    case "contextCompaction":
                        items.Add(new HistoryNotice("Context compacted"));
                        break;

                    case var type when CodexTools.IsTool(type):
                        foreach (var call in CodexTools.Calls(item, cwd))
                        {
                            items.Add(new HistoryToolCall(call.Id, call.Name, call.Input));
                        }

                        foreach (var result in CodexTools.Results(item))
                        {
                            items.Add(new HistoryToolResult(result.Id, result.IsError, result.Text, result.Structured));
                        }

                        break;
                }
            }

            switch (CodexJson.Str(turn, "status"))
            {
                case "interrupted":
                    items.Add(new HistoryNotice("Interrupted"));
                    break;
                case "failed":
                    items.Add(new HistoryNotice(CodexTranslator.ErrorText(CodexJson.Obj(turn, "error")) ?? "Error"));
                    break;
            }
        }

        return new SessionHistory { SessionId = threadId, WorkingDirectory = cwd, Model = CodexJson.Str(thread, "model"), Items = items };
    }

    /// <summary>A prompt's text and its pasted images (data URLs).</summary>
    internal static (string Text, List<HistoryImage> Images) UserContent(JsonElement item)
    {
        var text = new List<string>();
        var images = new List<HistoryImage>();
        foreach (var part in CodexJson.Arr(item, "content"))
        {
            switch (CodexJson.Str(part, "type"))
            {
                case "text" when CodexJson.Str(part, "text") is { Length: > 0 } t:
                    text.Add(t);
                    break;
                case "image" when CodexJson.Str(part, "url") is { } url && DataUrl(url) is { } image:
                    images.Add(image);
                    break;
            }
        }

        return (string.Join("\n", text), images);
    }

    private static HistoryImage? DataUrl(string url)
    {
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
