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

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryTag();
}
