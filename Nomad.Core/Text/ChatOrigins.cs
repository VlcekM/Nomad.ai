using System.Text.RegularExpressions;

namespace Nomad.Core.Text;

/// <summary>Who sent a prompt that came from another chat rather than the user, and what it says.</summary>
/// <param name="Name">The sending chat's name ("Meitou Bug Fixing").</param>
/// <param name="Detail">What it is, when known ("Antigravity", "Claude Code").</param>
/// <param name="Body">The message itself, without the wrapper.</param>
public sealed record ChatOrigin(string Name, string? Detail, string Body);

/// <summary>
/// Recognises prompts one chat sent another, so the transcript can show them as such rather than as the user's own words:
/// Claude Code's <c>&lt;cross-session-message&gt;</c> (a local session's SendMessage) and Nomad's <c>send_to_chat</c>.
/// </summary>
public static partial class ChatOrigins
{
    public static ChatOrigin? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 200_000)
        {
            return null;
        }

        if (CrossSession().Match(text) is { Success: true } cross)
        {
            var attributes = Attribute().Matches(cross.Groups["attrs"].Value)
                .ToDictionary(a => a.Groups[1].Value, a => a.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
            var name = attributes.GetValueOrDefault("from-name") is { Length: > 0 } n ? Unescape(n) : "Another Claude Code session";
            return new ChatOrigin(name, "Claude Code", cross.Groups["body"].Value);
        }

        if (NomadNamed().Match(text) is { Success: true } named)
        {
            return new ChatOrigin(named.Groups["name"].Value, named.Groups["agent"].Value, named.Groups["body"].Value);
        }

        if (NomadAnonymous().Match(text) is { Success: true } anonymous)
        {
            return new ChatOrigin("Another chat", null, anonymous.Groups["body"].Value);
        }

        return null;
    }

    private static string Unescape(string value) =>
        value.Replace("&quot;", "\"").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&apos;", "'").Replace("&amp;", "&");

    [GeneratedRegex(@"\A\s*<cross-session-message\b(?<attrs>[^>]*)>\s*(?<body>.*?)\s*</cross-session-message>\s*\z", RegexOptions.Singleline)]
    private static partial Regex CrossSession();

    [GeneratedRegex(@"([\w-]+)=""([^""]*)""")]
    private static partial Regex Attribute();

    // Nomad's send_to_chat envelope: Message from chat "X" (Agent, id …), sent with Nomad's send_to_chat. …\n\nbody
    [GeneratedRegex(@"\AMessage from chat ""(?<name>[^\r\n]*?)"" \((?<agent>[^,()\r\n]+), id [^)\r\n]*\), sent with Nomad's send_to_chat\.[^\r\n]*\r?\n\r?\n(?<body>.*)\z", RegexOptions.Singleline)]
    private static partial Regex NomadNamed();

    [GeneratedRegex(@"\AMessage from another chat, sent with Nomad's send_to_chat[^\r\n]*\r?\n\r?\n(?<body>.*)\z", RegexOptions.Singleline)]
    private static partial Regex NomadAnonymous();
}
