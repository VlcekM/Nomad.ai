using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nomad.Core.Agents;

/// <summary>A phase of a workflow run, as its script names it (numbered from 1).</summary>
public sealed record WorkflowPhase(int Index, string Title);

/// <summary>
/// One agent of a workflow run, as the CLI last reported it: queued (no id, not started yet), running, or done.
/// </summary>
public sealed record WorkflowAgentState(
    int Index,
    string Label,
    int PhaseIndex,
    string? AgentId,
    string? Model,
    string State,
    bool IsQueued,
    int Attempt,
    string? LastToolName,
    string? LastToolSummary,
    string? PromptPreview,
    long Tokens,
    int ToolCalls,
    long DurationMs,
    string? ResultPreview)
{
    public bool IsDone => State is "done" or "completed";

    public bool IsFailed => State is "failed" or "error" or "killed";

    public bool IsRunning => !IsQueued && !IsDone && !IsFailed;
}

/// <summary>
/// A workflow run's phases and agents: the <c>workflow_progress</c> array of a <c>task_progress</c> line, or the
/// <c>workflowProgress</c> the run's file keeps. Each line carries the whole run so far, not a change.
/// </summary>
public sealed record WorkflowProgress(IReadOnlyList<WorkflowPhase> Phases, IReadOnlyList<WorkflowAgentState> Agents)
{
    public static WorkflowProgress Parse(JsonElement array)
    {
        var phases = new List<WorkflowPhase>();
        var agents = new List<WorkflowAgentState>();
        if (array.ValueKind != JsonValueKind.Array)
        {
            return new WorkflowProgress(phases, agents);
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            switch (Str(item, "type"))
            {
                case "workflow_phase":
                    phases.Add(new WorkflowPhase(Int(item, "index"), Str(item, "title") ?? ""));
                    break;
                case "workflow_agent":
                    var agentId = Str(item, "agentId");
                    agents.Add(new WorkflowAgentState(
                        Int(item, "index"),
                        Str(item, "label") ?? $"agent {Int(item, "index")}",
                        Int(item, "phaseIndex"),
                        agentId,
                        Str(item, "model"),
                        Str(item, "state") ?? "",
                        agentId is null || !item.TryGetProperty("startedAt", out _),
                        Math.Max(1, Int(item, "attempt")),
                        Str(item, "lastToolName"),
                        Str(item, "lastToolSummary"),
                        Str(item, "promptPreview"),
                        Long(item, "tokens"),
                        Int(item, "toolCalls"),
                        Long(item, "durationMs"),
                        Str(item, "resultPreview")));
                    break;
            }
        }

        return new WorkflowProgress(phases, agents);
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    internal static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}

/// <summary>
/// What the Workflow call's result says about the run it launched ("Workflow launched in background. Task ID: …").
/// </summary>
public sealed partial record WorkflowLaunch(string? TaskId, string? RunId, string? TranscriptDir, string? ScriptFile)
{
    public static WorkflowLaunch? Parse(string? text)
    {
        if (text is null || !text.Contains("Workflow launched", StringComparison.Ordinal))
        {
            return null;
        }

        return new WorkflowLaunch(Line(TaskIdLine(), text), Line(RunIdLine(), text), Line(TranscriptLine(), text), Line(ScriptLine(), text));
    }

    /// <summary>
    /// The file the CLI keeps the run in once it ends (its result, phases and agents): next to the run's transcripts,
    /// in <c>&lt;session&gt;/workflows/&lt;run&gt;.json</c>.
    /// </summary>
    public string? RunFile
    {
        get
        {
            if (RunId is null || TranscriptDir is null)
            {
                return null;
            }

            // <session>\subagents\workflows\<run> → <session>
            var session = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(TranscriptDir.TrimEnd('\\', '/'))));
            return session is null ? null : Path.Combine(session, "workflows", RunId + ".json");
        }
    }

    private static string? Line(Regex regex, string text) => regex.Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    [GeneratedRegex(@"Task ID:\s*([\w-]+)")]
    private static partial Regex TaskIdLine();

    [GeneratedRegex(@"Run ID:\s*([\w-]+)")]
    private static partial Regex RunIdLine();

    [GeneratedRegex(@"^Transcript dir:\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex TranscriptLine();

    [GeneratedRegex(@"^Script file:\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex ScriptLine();
}

/// <summary>A finished workflow run, as the CLI keeps it in <c>workflows/&lt;run&gt;.json</c>.</summary>
public sealed record WorkflowRunRecord(
    string? RunId,
    string? Name,
    string? Summary,
    string? Status,
    string? Result,
    long DurationMs,
    long TotalTokens,
    int TotalToolCalls,
    IReadOnlyList<string> PhaseTitles,
    WorkflowProgress Progress)
{
    public static WorkflowRunRecord? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            return Parse(doc.RootElement);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static WorkflowRunRecord? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var titles = new List<string>();
        if (root.TryGetProperty("phases", out var phases) && phases.ValueKind == JsonValueKind.Array)
        {
            titles.AddRange(phases.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.Object ? WorkflowProgress.Str(p, "title") : null).OfType<string>());
        }

        var progress = root.TryGetProperty("workflowProgress", out var array)
            ? WorkflowProgress.Parse(array)
            : new WorkflowProgress([], []);
        return new WorkflowRunRecord(
            WorkflowProgress.Str(root, "runId"),
            WorkflowProgress.Str(root, "workflowName"),
            WorkflowProgress.Str(root, "summary"),
            WorkflowProgress.Str(root, "status"),
            root.TryGetProperty("result", out var result) ? Format(result) : null,
            WorkflowProgress.Long(root, "durationMs"),
            WorkflowProgress.Long(root, "totalTokens") is > 0 and var tokens ? tokens : progress.Agents.Sum(a => a.Tokens),
            WorkflowProgress.Int(root, "totalToolCalls") is > 0 and var calls ? calls : progress.Agents.Sum(a => a.ToolCalls),
            titles,
            progress);
    }

    /// <summary>A result as text: a string as it is, anything else as indented JSON; null for none.</summary>
    public static string? Format(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                return value.GetString();
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>A workflow script's <c>export const meta = {…}</c>: its name, description and phase titles.</summary>
public static partial class WorkflowScript
{
    public static (string? Name, string? Description, IReadOnlyList<string> Phases) ReadMeta(string? script)
    {
        if (script is null || MetaBlock().Match(script) is not { Success: true } meta)
        {
            return (null, null, []);
        }

        var body = meta.Groups[1].Value;
        var phases = PhasesList().Match(body) is { Success: true } list
            ? TitleField().Matches(list.Groups[1].Value).Select(m => Unquote(m.Groups[2].Value, m.Groups[1].Value)).ToList()
            : [];
        return (Field(body, "name"), Field(body, "description"), phases);
    }

    private static string? Field(string body, string name) =>
        Regex.Match(body, $@"\b{name}\s*:\s*(['""`])((?:\\.|(?!\1).)*)\1", RegexOptions.Singleline) is { Success: true } m
            ? Unquote(m.Groups[2].Value, m.Groups[1].Value)
            : null;

    private static string Unquote(string value, string quote) => value.Replace("\\" + quote, quote).Replace("\\n", "\n").Replace("\\\\", "\\");

    [GeneratedRegex(@"export\s+const\s+meta\s*=\s*\{(.*?)\}\s*;?[ \t]*(?:\r?\n|$)", RegexOptions.Singleline)]
    private static partial Regex MetaBlock();

    [GeneratedRegex(@"\bphases\s*:\s*\[(.*?)\]", RegexOptions.Singleline)]
    private static partial Regex PhasesList();

    [GeneratedRegex(@"\btitle\s*:\s*(['""`])((?:\\.|(?!\1).)*)\1", RegexOptions.Singleline)]
    private static partial Regex TitleField();
}
