using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nomad.Core.Codex;

/// <summary>One tool call a Codex item stands for, in the Claude Code names and fields the transcript knows.</summary>
public sealed record CodexCall(string Id, string Name, JsonElement Input);

/// <summary>A finished call's result.</summary>
public sealed record CodexResult(string Id, bool IsError, string Text, JsonElement Structured)
{
    public IReadOnlyList<string> Images { get; init; } = [];
}

/// <summary>
/// Maps Codex's thread items onto the tool calls the transcript draws (a <c>commandExecution</c> → <c>PowerShell</c>
/// or <c>Bash</c>, or <c>Read</c> / <c>Grep</c> / <c>LS</c> when Codex parsed it as one; each change of a
/// <c>fileChange</c> → <c>Edit</c> or <c>Write</c>; an MCP call → <c>mcp__server__tool</c>), so their cards, diffs
/// and activity lines need nothing of their own.
/// </summary>
public static partial class CodexTools
{
    /// <summary>The item is a tool call (it has calls once started).</summary>
    public static bool IsTool(string? type) =>
        type is "commandExecution" or "fileChange" or "mcpToolCall" or "dynamicToolCall" or "webSearch" or "imageView" or "collabAgentToolCall";

    /// <summary>The calls an item stands for: one, or one per file for a <c>fileChange</c>.</summary>
    public static IReadOnlyList<CodexCall> Calls(JsonElement item, string? cwd)
    {
        var id = CodexJson.Str(item, "id") ?? "";
        switch (CodexJson.Str(item, "type"))
        {
            case "commandExecution":
                return [Command(id, item, cwd)];

            case "fileChange":
            {
                var calls = new List<CodexCall>();
                var i = 0;
                foreach (var change in CodexJson.Arr(item, "changes"))
                {
                    calls.Add(Change(ChangeId(id, i++), change, cwd));
                }

                return calls;
            }

            case "mcpToolCall":
                return [new CodexCall(id, $"mcp__{CodexJson.Str(item, "server")}__{CodexJson.Str(item, "tool")}", ObjectOrEmpty(CodexJson.Any(item, "arguments")))];

            case "dynamicToolCall":
                return [new CodexCall(id, CodexJson.Str(item, "tool") ?? "tool", ObjectOrEmpty(CodexJson.Any(item, "arguments")))];

            case "webSearch":
            {
                var query = CodexJson.Str(item, "query");
                if (string.IsNullOrEmpty(query))
                {
                    var action = CodexJson.Obj(item, "action");
                    query = CodexJson.Str(action, "query") ?? CodexJson.Strings(action, "queries").FirstOrDefault() ?? CodexJson.Str(action, "url") ?? "";
                }

                return [new CodexCall(id, "WebSearch", CodexJson.ToElement(new JsonObject { ["query"] = query }))];
            }

            case "imageView":
                return [new CodexCall(id, "Read", CodexJson.ToElement(new JsonObject { ["file_path"] = FullPath(CodexJson.Str(item, "path"), cwd) }))];

            case "collabAgentToolCall":
                return [new CodexCall(id, CodexJson.Str(item, "tool") ?? "agent", CodexJson.ToElement(new JsonObject { ["prompt"] = CodexJson.Str(item, "prompt") ?? "" }))];

            default:
                return [];
        }
    }

    /// <summary>The results of a finished item's calls; none while it runs.</summary>
    public static IReadOnlyList<CodexResult> Results(JsonElement item, string? cwd = null)
    {
        var id = CodexJson.Str(item, "id") ?? "";
        var status = CodexJson.Str(item, "status");
        switch (CodexJson.Str(item, "type"))
        {
            case "commandExecution" when status is "completed" or "failed" or "declined":
            {
                var output = (CodexJson.Str(item, "aggregatedOutput") ?? "").TrimEnd();
                var exit = CodexJson.Long(item, "exitCode");
                if (status == "declined")
                {
                    output = "The command was declined.";
                }
                else if (output.Length == 0 && exit is { } code and not 0)
                {
                    output = $"Exit code {code}";
                }

                return [new CodexResult(id, status != "completed", output, default)];
            }

            case "fileChange" when status is "completed" or "failed" or "declined":
            {
                var results = new List<CodexResult>();
                var i = 0;
                foreach (var change in CodexJson.Arr(item, "changes"))
                {
                    var path = CodexJson.Str(change, "path") ?? "";
                    var kind = CodexJson.Str(CodexJson.Obj(change, "kind"), "type");
                    var diff = CodexJson.Str(change, "diff") ?? "";
                    results.Add(status == "completed"
                        ? new CodexResult(ChangeId(id, i), false, kind switch
                        {
                            "add" => $"Created {path}",
                            "delete" => $"Deleted {path}",
                            _ => $"Updated {path}",
                        }, Patch(kind, diff))
                        : new CodexResult(ChangeId(id, i), true, status == "declined" ? "The change was declined." : "The change could not be applied.", default));
                    i++;
                }

                return results;
            }

            case "mcpToolCall" when status is "completed" or "failed":
            {
                var error = CodexJson.Str(CodexJson.Obj(item, "error"), "message");
                var result = CodexJson.Obj(item, "result");
                var text = error ?? string.Join("\n", CodexJson.Arr(result, "content").Select(c => CodexJson.Str(c, "text")).OfType<string>());
                return [new CodexResult(id, status == "failed" || CodexJson.Bool(result, "isError") == true, text, CodexJson.Any(result, "structuredContent"))
                {
                    Images = ContentImages(CodexJson.Arr(result, "content")),
                }];
            }

            case "dynamicToolCall" when status is "completed" or "failed":
            {
                var text = string.Join("\n", CodexJson.Arr(item, "contentItems").Select(c => CodexJson.Str(c, "text")).OfType<string>());
                return [new CodexResult(id, status == "failed" || CodexJson.Bool(item, "success") == false, text, default)
                {
                    Images = ContentImages(CodexJson.Arr(item, "contentItems")),
                }];
            }

            case "collabAgentToolCall" when status is "completed" or "failed":
                return [new CodexResult(id, status == "failed", "", default)];

            case "imageView":
                return [new CodexResult(id, false, "", default)
                {
                    Images = CodexJson.Str(item, "path") is { Length: > 0 } imagePath ? [FullPath(imagePath, cwd)] : [],
                }];

            case "webSearch":
                return [new CodexResult(id, false, "", default)];

            default:
                return [];
        }
    }

    private static IReadOnlyList<string> ContentImages(IEnumerable<JsonElement> content)
    {
        var images = new List<string>();
        foreach (var part in content)
        {
            if (CodexJson.Str(part, "type") is not ("image" or "inputImage" or "outputImage"))
            {
                continue;
            }

            if ((CodexJson.Str(part, "imageUrl") ?? CodexJson.Str(part, "url")) is { Length: > 0 } url)
            {
                images.Add(url);
            }
            else if (CodexJson.Str(part, "data") is { Length: > 0 } data && CodexJson.Str(part, "mimeType") is { } mime && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                images.Add($"data:{mime};base64,{data}");
            }
        }

        return images;
    }

    /// <summary>The id of a <c>fileChange</c>'s <paramref name="index"/>th change: the item's own for the first.</summary>
    public static string ChangeId(string itemId, int index) => index == 0 ? itemId : $"{itemId}#{index}";

    /// <summary>The PowerShell / shell command inside the wrapper Codex runs it in (<c>powershell.exe -Command '…'</c>).</summary>
    public static (string Shell, string Command) Unwrap(string command)
    {
        var m = Wrapper().Match(command.Trim());
        if (!m.Success)
        {
            return ("", command);
        }

        var inner = m.Groups["cmd"].Value.Trim();
        if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"') && inner[^1] == inner[0])
        {
            inner = inner[1..^1];
            inner = m.Groups["shell"].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase)
                ? inner.Replace("''", "'").Replace("\\\"", "\"")
                : inner.Replace("'\\''", "'");
        }

        return (m.Groups["shell"].Value.ToLowerInvariant(), inner);
    }

    private static CodexCall Command(string id, JsonElement item, string? cwd)
    {
        var (shell, unwrapped) = Unwrap(CodexJson.Str(item, "command") ?? "");
        var actions = CodexJson.Arr(item, "commandActions").ToList();
        var name = shell is "powershell" or "pwsh" ? "PowerShell" : "Bash";
        if (actions.Count == 1)
        {
            var action = actions[0];
            switch (CodexJson.Str(action, "type"))
            {
                case "read" when CodexJson.Str(action, "path") is { Length: > 0 } path:
                    return new CodexCall(id, "Read", CodexJson.ToElement(new JsonObject { ["file_path"] = FullPath(path, CodexJson.Str(item, "cwd") ?? cwd) }));
                case "search" when CodexJson.Str(action, "query") is { Length: > 0 } query:
                    return new CodexCall(id, "Grep", CodexJson.ToElement(new JsonObject { ["pattern"] = query, ["path"] = CodexJson.Str(action, "path") }));
                case "listFiles":
                    return new CodexCall(id, "LS", CodexJson.ToElement(new JsonObject { ["path"] = FullPath(CodexJson.Str(action, "path") ?? ".", CodexJson.Str(item, "cwd") ?? cwd) }));
            }
        }

        // What Codex parsed out of the wrapper reads best; else the wrapper's inside.
        var shown = actions.Count > 0
            ? string.Join("\n", actions.Select(a => CodexJson.Str(a, "command")).OfType<string>().Distinct())
            : unwrapped;
        return new CodexCall(id, name, CodexJson.ToElement(new JsonObject { ["command"] = shown.Length > 0 ? shown : unwrapped }));
    }

    private static CodexCall Change(string id, JsonElement change, string? cwd)
    {
        var path = FullPath(CodexJson.Str(change, "path"), cwd);
        var kind = CodexJson.Obj(change, "kind");
        var diff = CodexJson.Str(change, "diff") ?? "";
        switch (CodexJson.Str(kind, "type"))
        {
            case "add":
                return new CodexCall(id, "Write", CodexJson.ToElement(new JsonObject { ["file_path"] = path, ["content"] = diff }));
            case "delete":
                return new CodexCall(id, "Delete", CodexJson.ToElement(new JsonObject { ["file_path"] = path }));
            default:
            {
                var (removed, added) = SplitDiff(diff);
                var input = new JsonObject { ["file_path"] = path, ["old_string"] = removed, ["new_string"] = added };
                if (CodexJson.Str(kind, "move_path") is { Length: > 0 } moved)
                {
                    input["move_to"] = FullPath(moved, cwd);
                }

                return new CodexCall(id, "Edit", CodexJson.ToElement(input));
            }
        }
    }

    /// <summary>A change's result in the shape Claude Code reports one, for the +/− counts.</summary>
    private static JsonElement Patch(string? kind, string diff)
    {
        if (kind == "add")
        {
            return CodexJson.ToElement(new JsonObject { ["type"] = "create", ["content"] = diff });
        }

        var lines = new JsonArray();
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+') || line.StartsWith('-'))
            {
                lines.Add((JsonNode)JsonValue.Create(line));
            }
        }

        return CodexJson.ToElement(new JsonObject { ["structuredPatch"] = new JsonArray((JsonNode)new JsonObject { ["lines"] = lines }) });
    }

    /// <summary>A unified diff's removed and added lines, as the old and new text an Edit card shows.</summary>
    internal static (string Removed, string Added) SplitDiff(string diff)
    {
        var removed = new List<string>();
        var added = new List<string>();
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('-'))
            {
                removed.Add(line[1..]);
            }
            else if (line.StartsWith('+'))
            {
                added.Add(line[1..]);
            }
        }

        return (string.Join('\n', removed), string.Join('\n', added));
    }

    private static JsonElement ObjectOrEmpty(JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.Clone() : CodexJson.ToElement(new JsonObject());

    internal static string FullPath(string? path, string? cwd)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(cwd) || Path.IsPathRooted(path))
        {
            return path ?? "";
        }

        try
        {
            return Path.GetFullPath(Path.Combine(cwd, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    // "C:\…\powershell.exe" -Command '…'   |   /bin/bash -lc '…'   |   cmd.exe /c …
    [GeneratedRegex("""^"?(?:[^"]*[\\/])?(?<shell>powershell|pwsh|bash|zsh|sh|cmd)(?:\.exe)?"?\s+(?:-NoProfile\s+)?(?:-Command|-lc|-c|/c)\s+(?<cmd>[\s\S]+)$""", RegexOptions.IgnoreCase)]
    private static partial Regex Wrapper();
}
