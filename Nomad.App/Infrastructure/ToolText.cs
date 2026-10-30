using System.Text;
using System.Text.Json;

namespace Nomad.App.Infrastructure;

public enum ToolCategory
{
    Command,
    Edit,
    Read,
    Search,
    Web,
    Agent,
    Todo,
    Question,
    Other,
}

/// <summary>Human wording for tool calls: one-line summaries, group lines, icons, diff stats.</summary>
public static class ToolText
{
    public static ToolCategory CategoryOf(string name) => name switch
    {
        "Bash" or "PowerShell" or "BashOutput" or "KillShell" => ToolCategory.Command,
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => ToolCategory.Edit,
        "Read" or "NotebookRead" => ToolCategory.Read,
        "Grep" or "Glob" or "LS" => ToolCategory.Search,
        "WebFetch" or "WebSearch" => ToolCategory.Web,
        "Task" or "Agent" => ToolCategory.Agent,
        "TodoWrite" => ToolCategory.Todo,
        "AskUserQuestion" => ToolCategory.Question,
        "mcp__nomad_session__request_folder_move" => ToolCategory.Question,
        _ => ToolCategory.Other,
    };

    public static string IconKey(string name) => CategoryOf(name) switch
    {
        ToolCategory.Command => "IconTerminal",
        ToolCategory.Edit => "IconPencil",
        ToolCategory.Read => "IconFile",
        ToolCategory.Search => "IconSearch",
        ToolCategory.Web => "IconGlobe",
        ToolCategory.Agent => "IconAgent",
        ToolCategory.Todo => "IconList",
        ToolCategory.Question => "IconQuestion",
        _ => "IconTool",
    };

    /// <summary>"Running a command…" while a call is in flight.</summary>
    // ------------------------------------------------------------------ working mark

    private static readonly HashSet<string> WriteWords = ["write", "edit", "create", "update", "delete", "remove", "append", "insert", "replace", "patch", "send", "post", "add", "save", "upload", "comment"];
    private static readonly HashSet<string> CodeWords = ["bash", "exec", "execute", "run", "shell", "python", "repl", "script", "eval", "javascript"];
    private static readonly HashSet<string> ReadWords = ["read", "view", "get", "list", "fetch", "search", "find", "query", "retrieve", "lookup", "load", "open", "ls"];
    private static readonly HashSet<string> ProseExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".mdx", ".txt", ".rst", ".docx", ".pdf" };

    /// <summary>
    /// The working mark's activity for a running tool, as the desktop app picks it: "read", "search", "code",
    /// "write" ("compose" while writing prose), else "default". A command whose description starts with a writing
    /// or reading verb ("Updating the README") counts as that.
    /// </summary>
    public static string MarkActivity(string name, JsonElement input)
    {
        switch (name)
        {
            case "Read" or "NotebookRead" or "Grep" or "Glob" or "LS":
                return "read";
            case "WebSearch" or "WebFetch":
                return "search";
            case "Bash" or "PowerShell" or "BashOutput" or "KillShell" or "KillBash":
                return VerbOf(String(input, "description")) ?? "code";
            case "Edit" or "MultiEdit" or "Write" or "NotebookEdit":
                return String(input, "file_path") is { } path && ProseExtensions.Contains(Path.GetExtension(path)) ? "compose" : "write";
            case "TodoWrite" or "ExitPlanMode":
                return "write";
        }

        // MCP and other tools: by the words of their name ("mcp__github__create_issue" writes).
        foreach (var word in name.ToLowerInvariant().Split(['_', '-', ':', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (WriteWords.Contains(word))
            {
                return "write";
            }

            if (CodeWords.Contains(word))
            {
                return "code";
            }

            if (ReadWords.Contains(word))
            {
                return "read";
            }
        }

        return "default";
    }

    // "Updating" → updating, updat, update (and "Running" → run): the forms the verb sets hold.
    private static string? VerbOf(string? description)
    {
        var first = description?.TrimStart().Split(' ', 2)[0].Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(first) || !first.All(char.IsLetter))
        {
            return null;
        }

        var forms = new List<string> { first };
        if (first.EndsWith("ing", StringComparison.Ordinal) && first.Length > 5)
        {
            var stem = first[..^3];
            forms.Add(stem);
            forms.Add(stem + "e");
            if (stem.Length > 2 && stem[^1] == stem[^2] && !"aeiou".Contains(stem[^1]))
            {
                forms.Add(stem[..^1]);
            }
        }

        return forms.Any(WriteWords.Contains) ? "write" : forms.Any(ReadWords.Contains) ? "read" : null;
    }

    private static string? String(JsonElement input, string property) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The working row while a tool runs: what exactly it does ("Building Nomad.App" from a command's description,
    /// "Reading MainViewModel.cs", "Searching for “TODO”"), the group's generic label when its input says nothing.
    /// </summary>
    public static string RunningAction(string name, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return RunningLabel(name);
        }

        string? File(string property) => Str(input, property) is { Length: > 0 } path ? Path.GetFileName(path.TrimEnd('\\', '/')) : null;
        string? Quoted(string property) => Str(input, property) is { Length: > 0 } text ? $"“{Clip(FirstLine(text), 60)}”" : null;

        var action = name switch
        {
            "Bash" or "PowerShell" => Str(input, "description") is { Length: > 0 } description
                ? FirstLine(description)
                : Str(input, "command") is { Length: > 0 } command ? "Running " + Clip(FirstLine(command), 60) : null,
            "Read" or "NotebookRead" => (File("file_path") ?? File("notebook_path")) is { } read ? "Reading " + read : null,
            "Edit" or "MultiEdit" or "NotebookEdit" => (File("file_path") ?? File("notebook_path")) is { } edited ? "Editing " + edited : null,
            "Write" => File("file_path") is { } written ? "Writing " + written : null,
            "Grep" => Quoted("pattern") is { } pattern ? "Searching for " + pattern : null,
            "Glob" => Quoted("pattern") is { } glob ? "Finding " + glob : null,
            "WebFetch" => Uri.TryCreate(Str(input, "url"), UriKind.Absolute, out var uri) ? "Fetching " + uri.Host : null,
            "WebSearch" => Quoted("query") is { } query ? "Searching the web for " + query : null,
            "Task" or "Agent" => Str(input, "description") is { Length: > 0 } task ? FirstLine(task) : null,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(action) ? RunningLabel(name) : action.TrimEnd('.') + "…";
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";

    public static string RunningLabel(string name) => name switch
    {
        "Bash" or "PowerShell" => "Running a command…",
        "Edit" or "MultiEdit" or "NotebookEdit" => "Editing a file…",
        "Write" => "Writing a file…",
        "Read" or "NotebookRead" => "Reading a file…",
        "Grep" or "Glob" or "LS" => "Searching…",
        "WebFetch" => "Fetching a page…",
        "WebSearch" => "Searching the web…",
        "Task" or "Agent" => "Running an agent…",
        "Workflow" => "Starting a workflow…",
        "TodoWrite" => "Updating the todo list…",
        "AskUserQuestion" => "Waiting for your answer…",
        "mcp__nomad_session__request_folder_move" => "Requesting a folder move…",
        _ => $"Using {name}…",
    };

    /// <summary>
    /// The collapsed line for a finished group: "Ran 2 commands", "Edited 2 files, ran 2 commands",
    /// "Used 6 tools" when the mix is too varied.
    /// </summary>
    public static string GroupSummary(IReadOnlyList<(string Name, bool Failed)> calls)
    {
        if (calls.Count == 0)
        {
            return "";
        }

        var order = new List<ToolCategory>();
        var counts = new Dictionary<ToolCategory, int>();
        foreach (var (name, _) in calls)
        {
            var c = CategoryOf(name);
            if (!counts.TryAdd(c, 1))
            {
                counts[c]++;
            }
            else
            {
                order.Add(c);
            }
        }

        // A skipped question comes back as an error result, but nothing failed.
        var failed = calls.Count(c => c.Failed && CategoryOf(c.Name) != ToolCategory.Question);
        string text;
        if (order.Count > 3)
        {
            text = "Used " + Format.Count(calls.Count, "tool", "tools");
        }
        else
        {
            var parts = order.Select(c => Phrase(c, counts[c])).ToList();
            text = Format.Capitalize(string.Join(", ", parts));
        }

        return failed > 0 ? $"{text} ({failed} failed)" : text;
    }

    private static string Phrase(ToolCategory category, int n) => category switch
    {
        ToolCategory.Command => "ran " + Format.Count(n, "command", "commands"),
        ToolCategory.Edit => "edited " + Format.Count(n, "file", "files"),
        ToolCategory.Read => "read " + Format.Count(n, "file", "files"),
        ToolCategory.Search => n == 1 ? "searched the code" : $"searched the code {n} times",
        ToolCategory.Web => "browsed the web",
        ToolCategory.Agent => "ran " + Format.Count(n, "agent", "agents", "an"),
        ToolCategory.Todo => "updated the todo list",
        ToolCategory.Question => "asked " + Format.Count(n, "question", "questions"),
        _ => "used " + Format.Count(n, "tool", "tools"),
    };

    /// <summary>One-line description of a call's input (command, path, pattern, …).</summary>
    public static string Summary(string name, JsonElement input, string? cwd)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        string? value = name switch
        {
            "Bash" or "PowerShell" => Str(input, "command"),
            "Read" or "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "NotebookRead" => ShortPath(Str(input, "file_path") ?? Str(input, "notebook_path"), cwd),
            "Grep" => Join(Str(input, "pattern"), ShortPath(Str(input, "path"), cwd)),
            "Glob" => Join(Str(input, "pattern"), ShortPath(Str(input, "path"), cwd)),
            "LS" => ShortPath(Str(input, "path"), cwd),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            "Task" or "Agent" => Str(input, "description") ?? Str(input, "prompt"),
            "Workflow" => Str(input, "name") ?? Nomad.Core.Agents.WorkflowScript.ReadMeta(Str(input, "script")).Name ?? (Str(input, "scriptPath") is { } script ? Path.GetFileNameWithoutExtension(script) : null),
            "TodoWrite" => "todo list",
            "AskUserQuestion" => FirstQuestion(input),
            _ => null,
        };

        value ??= FirstStringValue(input);
        return FirstLine(value ?? "");
    }

    /// <summary>Whether the summary should be shown in the monospace font.</summary>
    public static bool SummaryIsCode(string name) => CategoryOf(name) is ToolCategory.Command or ToolCategory.Search;

    /// <summary>Counts added/removed lines from an Edit/Write tool_use_result (structuredPatch or created content).</summary>
    public static (int Added, int Removed) DiffStat(string name, JsonElement structured)
    {
        if (structured.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }

        var added = 0;
        var removed = 0;
        if (structured.TryGetProperty("structuredPatch", out var patch) && patch.ValueKind == JsonValueKind.Array && patch.GetArrayLength() > 0)
        {
            foreach (var hunk in patch.EnumerateArray())
            {
                if (hunk.ValueKind != JsonValueKind.Object || !hunk.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var line in lines.EnumerateArray())
                {
                    var s = line.ValueKind == JsonValueKind.String ? line.GetString() : null;
                    if (s is null || s.Length == 0)
                    {
                        continue;
                    }

                    if (s[0] == '+')
                    {
                        added++;
                    }
                    else if (s[0] == '-')
                    {
                        removed++;
                    }
                }
            }

            return (added, removed);
        }

        if (name == "Write" && Str(structured, "type") == "create" && Str(structured, "content") is { } content)
        {
            added = content.Length == 0 ? 0 : content.TrimEnd('\n').Split('\n').Length;
        }

        return (added, removed);
    }

    /// <summary>Indented JSON for the expanded row.</summary>
    public static string PrettyJson(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? FirstQuestion(JsonElement input) =>
        input.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() > 0
            ? Str(questions[0], "question")
            : null;

    public static string? Str(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string ShortPath(string? path, string? cwd)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        if (!string.IsNullOrEmpty(cwd))
        {
            var root = cwd.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return path[root.Length..];
            }
        }

        return path;
    }

    public static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        return newline < 0 ? trimmed : trimmed[..newline] + " …";
    }

    private static string? Join(string? a, string? b) =>
        string.IsNullOrEmpty(b) ? a : string.IsNullOrEmpty(a) ? b : $"{a}  in {b}";

    private static string? FirstStringValue(JsonElement input)
    {
        foreach (var p in input.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
            {
                return p.Value.GetString();
            }
        }

        return null;
    }
}
