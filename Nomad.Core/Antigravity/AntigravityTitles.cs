using System.Text;
using System.Text.RegularExpressions;

namespace Nomad.Core.Antigravity;

/// <summary>
/// A Gemini chat's name as Antigravity keeps it: the <c>title</c> field of <c>annotations/&lt;id&gt;.pbtxt</c> in the
/// CLI's data folder, a one-line protobuf text record that also holds the chat's archive and last-viewed state. agy
/// reads it whenever it loads a chat (and reports it to ACP clients as the session's title), and it is where a
/// rename in Antigravity, or on antigravity.google.com through the remote-control daemon, ends up. agy does not
/// rewrite the file during a load or a turn, so a title written here stays.
/// </summary>
public static partial class AntigravityTitles
{
    /// <summary>
    /// agy's data folder, <c>~/.gemini/antigravity-cli</c> of whoever runs Nomad. Home is found as agy finds it
    /// (USERPROFILE on Windows, HOME elsewhere), so the two agree even when that variable is redirected.
    /// </summary>
    public static string DataFolder { get; set; } = Path.Combine(Home(), ".gemini", "antigravity-cli");

    private static string Home()
    {
        var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        return string.IsNullOrEmpty(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;
    }

    private static string FileOf(string conversationId) =>
        Path.Combine(DataFolder, "annotations", conversationId + ".pbtxt");

    /// <summary>The chat's Antigravity title, or null when it has none (or its record cannot be read).</summary>
    public static string? Read(string conversationId)
    {
        if (!IsConversationId(conversationId))
        {
            return null;
        }

        try
        {
            var file = FileOf(conversationId);
            if (!File.Exists(file))
            {
                return null;
            }

            var title = ParseTitle(File.ReadAllText(file, Encoding.UTF8));
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            // A record agy did not write (hand-edited, damaged) must not stop the sidebar refreshing.
            return null;
        }
    }

    /// <summary>
    /// Names the chat in Antigravity, keeping every other field of its record. Written to a temporary file that then
    /// replaces the old one, so agy never reads half a record. False when it could not be written, or the record's
    /// title is damaged (rewriting it could lose the rest).
    /// </summary>
    public static bool Write(string conversationId, string title)
    {
        if (!IsConversationId(conversationId) || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var file = FileOf(conversationId);
        var temp = file + "." + Environment.ProcessId + ".tmp";
        try
        {
            var old = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "";
            if (WithTitle(old, title) is not { } record)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(temp, record, new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Left in the annotations folder, where agy reads only *.pbtxt.
            }

            return false;
        }
    }

    /// <summary>The top-level <c>title</c> of a text record, unescaped; null when it has none or it is malformed.</summary>
    public static string? ParseTitle(string record)
    {
        var match = TitleField().Match(record);
        return match.Success && ReadString(record, match.Index + match.Length, out _) is { } title ? title : null;
    }

    /// <summary>
    /// The record with its title set: replaced where it was, or put first, leaving every other field as it was. Null
    /// when the record has a title that cannot be read, since where that field ends is then unknown and a second
    /// title would make agy reject the whole record.
    /// </summary>
    public static string? WithTitle(string record, string title)
    {
        var field = "title:\"" + Escape(title) + "\"";
        var match = TitleField().Match(record);
        if (match.Success)
        {
            return ReadString(record, match.Index + match.Length, out var end) is null
                ? null
                : string.Concat(record.AsSpan(0, match.Index), field, record.AsSpan(end));
        }

        var rest = record.Trim();
        return rest.Length == 0 ? field : field + "  " + rest;
    }

    /// <summary>Escapes a string for a protobuf text record (UTF-8 is kept as it is, as agy writes it).</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20 || ch == 0x7f)
                    {
                        sb.Append('\\').Append(Convert.ToString(ch, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads a double-quoted string starting just after its opening quote, as the protobuf text format escapes it
    /// (<c>\n \t \" \\ \'</c>, octal and <c>\x</c> bytes, <c>\u</c> and <c>\U</c>); <paramref name="end"/> is just
    /// after the closing quote. Null when it never closes or an escape is malformed.
    /// </summary>
    private static string? ReadString(string text, int start, out int end)
    {
        var bytes = new List<byte>(text.Length - start);
        var i = start;
        end = start;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '"')
            {
                end = i + 1;
                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            if (ch != '\\')
            {
                // Whole characters (surrogate pairs included) go through as UTF-8.
                var length = char.IsHighSurrogate(ch) && i + 1 < text.Length ? 2 : 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(text.Substring(i, length)));
                i += length;
                continue;
            }

            if (++i >= text.Length)
            {
                return null;
            }

            var escape = text[i++];
            switch (escape)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case 'v': bytes.Add(11); break;
                case '\\' or '\'' or '"' or '?': bytes.Add((byte)escape); break;
                case 'x' or 'X':
                    {
                        var digits = Digits(text, i, 2, 16);
                        if (digits == 0) return null;
                        bytes.Add((byte)Convert.ToInt32(text.Substring(i, digits), 16));
                        i += digits;
                        break;
                    }

                case 'u' or 'U':
                    {
                        var count = escape == 'u' ? 4 : 8;
                        if (Digits(text, i, count, 16) != count) return null;

                        // Eight hex digits can exceed int: read as long, so FFFFFFFF is out of range rather than -1.
                        var code = Convert.ToInt64(text.Substring(i, count), 16);
                        if (code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) return null;
                        bytes.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32((int)code)));
                        i += count;
                        break;
                    }

                case >= '0' and <= '7':
                    {
                        var digits = Digits(text, i - 1, 3, 8);
                        var value = Convert.ToInt32(text.Substring(i - 1, digits), 8);
                        if (value > 0xFF) return null;
                        bytes.Add((byte)value);
                        i += digits - 1;
                        break;
                    }

                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>How many digits of the base (at most <paramref name="max"/>) start at <paramref name="at"/>.</summary>
    private static int Digits(string text, int at, int max, int radix)
    {
        var n = 0;
        while (n < max && at + n < text.Length && IsDigit(text[at + n], radix))
        {
            n++;
        }

        return n;
    }

    private static bool IsDigit(char ch, int radix) => radix == 8
        ? ch is >= '0' and <= '7'
        : char.IsAsciiHexDigit(ch);

    /// <summary>A conversation id is a GUID; anything else could name a file outside the annotations folder.</summary>
    private static bool IsConversationId(string id) => Guid.TryParse(id, out _);

    /// <summary>
    /// The top-level title field up to its opening quote. agy writes the title first; a field of a nested message
    /// (<c>{...}</c>) is never named title, and a longer name ending in "title" is not matched.
    /// </summary>
    [GeneratedRegex(@"(?<![\w.])title\s*:\s*""", RegexOptions.CultureInvariant)]
    private static partial Regex TitleField();
}
