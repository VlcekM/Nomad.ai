using System.Text.RegularExpressions;

namespace Nomad.Core.Sessions;

/// <summary>
/// The CLI tells Claude a background task (a subagent, a background command) ended with a
/// <c>&lt;task-notification&gt;</c> message in the user's place. It is not a prompt: the transcript shows its
/// summary as a notice instead of a bubble.
/// </summary>
public static partial class TaskNotification
{
    public static bool TryParse(string? text, out string summary)
    {
        summary = "";
        if (text is null || !text.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal))
        {
            return false;
        }

        var match = SummaryTag().Match(text);
        summary = match.Success ? match.Groups[1].Value.Trim() : "Background task finished";
        return true;
    }

    /// <summary>The tool-use id of the task it reports on (a background subagent's Agent call), if given.</summary>
    public static string? ToolUseId(string text) => ToolUseIdTag().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    /// <summary>The task's own id ("b1x2…" for a background command), if given.</summary>
    public static string? TaskId(string text) => TaskIdTag().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    /// <summary>"completed", "failed", "killed": how it ended, lower-case; null when not given.</summary>
    public static string? Status(string text) => StatusTag().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim().ToLowerInvariant() : null;

    /// <summary>The task id a background command's call returned ("Command running in background with ID: b1x2…").</summary>
    public static string? LaunchedTaskId(string result) => LaunchedId().Match(result) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"<task-id>(.*?)</task-id>", RegexOptions.Singleline)]
    private static partial Regex TaskIdTag();

    [GeneratedRegex(@"<status>(.*?)</status>", RegexOptions.Singleline)]
    private static partial Regex StatusTag();

    [GeneratedRegex(@"in background with ID:\s*([\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LaunchedId();

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryTag();

    [GeneratedRegex(@"<tool-use-id>(.*?)</tool-use-id>", RegexOptions.Singleline)]
    private static partial Regex ToolUseIdTag();
}
