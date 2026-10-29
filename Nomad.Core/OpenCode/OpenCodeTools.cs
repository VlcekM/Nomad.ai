using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nomad.Core.OpenCode;

/// <summary>
/// Maps OpenCode's tools onto the Claude Code names and input fields the transcript knows (<c>edit</c> with
/// <c>filePath</c>/<c>oldString</c> → <c>Edit</c> with <c>file_path</c>/<c>old_string</c>), so its tool cards, diffs and
/// activity lines need nothing of their own. Tools without a counterpart (MCP tools, custom ones) keep their name and input.
/// </summary>
public static class OpenCodeTools
{
    public static (string Name, JsonElement Input) Map(string tool, JsonElement input, string? cwd)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return (Name(tool), input);
        }

        var o = new JsonObject();
        switch (tool)
        {
            case "bash":
                Copy(input, o, "command", "command");
                Copy(input, o, "description", "description");
                Copy(input, o, "timeout", "timeout");
                return ("Bash", ToElement(o));
            case "read":
                o["file_path"] = FullPath(OpenCodeJson.Str(input, "filePath"), cwd);
                Copy(input, o, "offset", "offset");
                Copy(input, o, "limit", "limit");
                return ("Read", ToElement(o));
            case "write":
                o["file_path"] = FullPath(OpenCodeJson.Str(input, "filePath"), cwd);
                Copy(input, o, "content", "content");
                return ("Write", ToElement(o));
            case "edit":
                o["file_path"] = FullPath(OpenCodeJson.Str(input, "filePath"), cwd);
                Copy(input, o, "oldString", "old_string");
                Copy(input, o, "newString", "new_string");
                Copy(input, o, "replaceAll", "replace_all");
                return ("Edit", ToElement(o));
            case "grep":
                Copy(input, o, "pattern", "pattern");
                Copy(input, o, "path", "path");
                Copy(input, o, "include", "glob");
                return ("Grep", ToElement(o));
            case "glob":
                Copy(input, o, "pattern", "pattern");
                Copy(input, o, "path", "path");
                return ("Glob", ToElement(o));
            case "list":
                Copy(input, o, "path", "path");
                return ("LS", ToElement(o));
            case "webfetch":
                Copy(input, o, "url", "url");
                Copy(input, o, "format", "format");
                return ("WebFetch", ToElement(o));
            case "websearch":
                Copy(input, o, "query", "query");
                return ("WebSearch", ToElement(o));
            case "task":
                Copy(input, o, "description", "description");
                Copy(input, o, "prompt", "prompt");
                Copy(input, o, "subagent_type", "subagent_type");
                return ("Task", ToElement(o));
            case "todowrite":
                return ("TodoWrite", Todos(input));
            case "question":
                return ("AskUserQuestion", Questions(input));
            default:
                return (Name(tool), input);
        }
    }

    /// <summary>The Claude Code name for an OpenCode tool name, or the name itself.</summary>
    public static string Name(string tool) => tool switch
    {
        "bash" => "Bash",
        "read" => "Read",
        "write" => "Write",
        "edit" or "patch" or "multiedit" => "Edit",
        "grep" => "Grep",
        "glob" => "Glob",
        "list" => "LS",
        "webfetch" => "WebFetch",
        "websearch" => "WebSearch",
        "task" => "Task",
        "todowrite" => "TodoWrite",
        "todoread" => "TodoRead",
        "question" => "AskUserQuestion",
        _ => tool,
    };

    /// <summary>
    /// A finished tool's result in the shape Claude Code reports it, as far as the transcript reads it: a
    /// <c>structuredPatch</c> (from OpenCode's unified diff) for the +/− counts of an edit.
    /// </summary>
    public static JsonElement Structured(JsonElement metadata)
    {
        if (OpenCodeJson.Str(metadata, "diff") is not { Length: > 0 } diff)
        {
            return default;
        }

        var lines = new JsonArray();
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+') || line.StartsWith('-'))
            {
                lines.Add((JsonNode)JsonValue.Create(line.TrimEnd('\r')));
            }
        }

        return ToElement(new JsonObject { ["structuredPatch"] = new JsonArray((JsonNode)new JsonObject { ["lines"] = lines }) });
    }

    /// <summary>
    /// The input of a tool waiting for permission, from what OpenCode sends with the request when the call itself
    /// wasn't seen (a file path, a command, a pattern).
    /// </summary>
    public static (string Name, JsonElement Input) FromPermission(string permission, IReadOnlyList<string> patterns, JsonElement metadata)
    {
        var o = new JsonObject();
        switch (permission)
        {
            case "edit":
                o["file_path"] = OpenCodeJson.Str(metadata, "filepath") ?? patterns.FirstOrDefault() ?? "";
                return ("Edit", ToElement(o));
            case "bash":
                o["command"] = OpenCodeJson.Str(metadata, "command") ?? string.Join(" ", patterns);
                return ("Bash", ToElement(o));
            case "webfetch":
                o["url"] = OpenCodeJson.Str(metadata, "url") ?? patterns.FirstOrDefault() ?? "";
                return ("WebFetch", ToElement(o));
            default:
                o["patterns"] = new JsonArray([.. patterns.Select(p => (JsonNode?)JsonValue.Create(p))]);
                return (Name(permission), ToElement(o));
        }
    }

    /// <summary>OpenCode's questions (<c>multiple</c>) as AskUserQuestion's (<c>multiSelect</c>).</summary>
    public static JsonElement Questions(JsonElement source)
    {
        var questions = new JsonArray();
        if (OpenCodeJson.Arr(source, "questions") is { } list)
        {
            foreach (var q in list.EnumerateArray())
            {
                var options = new JsonArray();
                if (OpenCodeJson.Arr(q, "options") is { } opts)
                {
                    foreach (var opt in opts.EnumerateArray())
                    {
                        options.Add((JsonNode)new JsonObject { ["label"] = OpenCodeJson.Str(opt, "label"), ["description"] = OpenCodeJson.Str(opt, "description") });
                    }
                }

                questions.Add((JsonNode)new JsonObject
                {
                    ["question"] = OpenCodeJson.Str(q, "question"),
                    ["header"] = OpenCodeJson.Str(q, "header"),
                    ["multiSelect"] = q.TryGetProperty("multiple", out var m) && m.ValueKind == JsonValueKind.True,
                    ["options"] = options,
                });
            }
        }

        return ToElement(new JsonObject { ["questions"] = questions });
    }

    /// <summary>
    /// AskUserQuestion's answers (question text → "A" or "A, B") back in OpenCode's form: per question, in order, the
    /// labels chosen, or the typed answer.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Answers(JsonElement questionsInput, JsonElement updatedInput)
    {
        var answers = OpenCodeJson.Obj(updatedInput, "answers");
        var result = new List<IReadOnlyList<string>>();
        if (OpenCodeJson.Arr(questionsInput, "questions") is not { } questions)
        {
            return result;
        }

        foreach (var q in questions.EnumerateArray())
        {
            var text = OpenCodeJson.Str(q, "question") ?? "";
            var answer = OpenCodeJson.Str(answers, text) ?? "";
            var labels = OpenCodeJson.Arr(q, "options") is { } opts
                ? opts.EnumerateArray().Select(o => OpenCodeJson.Str(o, "label")).OfType<string>().ToList()
                : [];
            var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;

            // A multi-select answer joins its labels with ", "; split it back only when every piece is one of them.
            var pieces = answer.Split(", ");
            result.Add(multi && pieces.Length > 1 && pieces.All(labels.Contains) ? pieces : [answer]);
        }

        return result;
    }

    private static JsonElement Todos(JsonElement input)
    {
        var todos = new JsonArray();
        if (OpenCodeJson.Arr(input, "todos") is { } list)
        {
            foreach (var t in list.EnumerateArray())
            {
                var content = OpenCodeJson.Str(t, "content") ?? "";
                todos.Add((JsonNode)new JsonObject
                {
                    ["content"] = content,
                    ["status"] = OpenCodeJson.Str(t, "status") is "cancelled" ? "completed" : OpenCodeJson.Str(t, "status") ?? "pending",
                    ["activeForm"] = content,
                });
            }
        }

        return ToElement(new JsonObject { ["todos"] = todos });
    }

    private static void Copy(JsonElement from, JsonObject to, string name, string as_)
    {
        if (from.TryGetProperty(name, out var v) && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            to[as_] = JsonNode.Parse(v.GetRawText());
        }
    }

    private static string FullPath(string? path, string? cwd)
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

    internal static JsonElement ToElement(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}
