using System.Text.RegularExpressions;

namespace Nomad.Core.Sessions;

/// <summary>
/// After a slash command (<c>/model</c>, <c>/cost</c>…) the CLI replays what it recorded in the user's place: the
/// command (<c>&lt;command-name&gt;</c>), a caveat for Claude, and the command's output
/// (<c>&lt;local-command-stdout&gt;</c>). None of it is a prompt; the output alone is worth a notice.
/// </summary>
public static partial class LocalCommandEcho
{
    /// <summary>True for such an echo; <paramref name="output"/> is the command's output, or null when there is none to show.</summary>
    public static bool TryParse(string? text, out string? output)
    {
        output = null;
        var trimmed = text?.TrimStart();
        if (trimmed is null || !(trimmed.StartsWith("<local-command-", StringComparison.Ordinal) || trimmed.StartsWith("<command-", StringComparison.Ordinal)))
        {
            return false;
        }

        var stdout = Stdout().Match(trimmed);
        if (stdout.Success)
        {
            var value = Ansi().Replace(stdout.Groups[1].Value, "").Replace("`", "", StringComparison.Ordinal).Trim();
            output = value.Length > 0 ? value : null;
        }

        return true;
    }

    [GeneratedRegex(@"<local-command-stdout>(.*?)</local-command-stdout>", RegexOptions.Singleline)]
    private static partial Regex Stdout();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();
}
