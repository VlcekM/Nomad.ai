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

    /// <summary>
    /// The user's home folder, as agy finds it.
    /// </summary>
    private static string Home()
    {
        var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        return string.IsNullOrEmpty(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;
    }

    /// <summary>
    /// The annotation record of a conversation.
    /// </summary>
    private static string FileOf(string conversationId) =>
        Path.Combine(DataFolder, "annotations", conversationId + ".pbtxt");

    /// <summary>
    /// The chat's Antigravity title, or null when it has none (or its record cannot be read).
    /// </summary>
    public static string? Read(string conversationId)
    {
        if (!IsConversationId(conversationId)) return null;

        try
        {
            var file = FileOf(conversationId);
            if (!File.Exists(file)) return null;

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
        if (!IsConversationId(conversationId) || string.IsNullOrWhiteSpace(title)) return false;

        var file = FileOf(conversationId);
        var temp = file + "." + Environment.ProcessId + ".tmp";
        try
        {
            var old = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "";
            if (WithTitle(old, title) is not { } record) return false;

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

    /// <summary>
    /// The top-level <c>title</c> of a text record, unescaped; null when it has none or it is malformed.
    /// </summary>
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

    /// <summary>
    /// Escapes a string for a protobuf text record (UTF-8 is kept as it is, as agy writes it).
    /// </summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            sb.Append(EscapeChar(ch));
        }

        return sb.ToString();
    }

    /// <summary>
    /// One character as a text record writes it: a control character escaped (named or octal), anything else as it is.
    /// </summary>
    private static string EscapeChar(char ch) => ch switch
    {
        '\\' => "\\\\",
        '"' => "\\\"",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        < (char)0x20 or (char)0x7f => "\\" + Convert.ToString(ch, 8).PadLeft(3, '0'),
        _ => ch.ToString(),
    };

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

            if (++i >= text.Length) return null;

            var escape = text[i++];
            if (ReadEscape(text, escape, i, bytes) is not { } read) return null;

            i += read;
        }

        return null;
    }

    /// <summary>
    /// Adds the bytes of one escape (its letter <paramref name="escape"/>, then whatever follows from <paramref name="at"/>);
    /// returns how many characters past the letter it used, or null when it is malformed.
    /// </summary>
    private static int? ReadEscape(string text, char escape, int at, List<byte> bytes)
    {
        if (SimpleEscape(escape) is { } simple)
        {
            bytes.Add(simple);
            return 0;
        }

        return escape switch
        {
            'x' or 'X' => ReadHexByte(text, at, bytes),
            'u' => ReadCodePoint(text, at, 4, bytes),
            'U' => ReadCodePoint(text, at, 8, bytes),
            >= '0' and <= '7' => ReadOctalByte(text, at - 1, bytes) is { } digits ? digits - 1 : null,
            _ => null,
        };
    }

    /// <summary>
    /// The byte a one-letter escape stands for, or null when the letter is not one.
    /// </summary>
    private static byte? SimpleEscape(char escape) => escape switch
    {
        'n' => (byte)'\n',
        'r' => (byte)'\r',
        't' => (byte)'\t',
        'a' => 7,
        'b' => 8,
        'f' => 12,
        'v' => 11,
        '\\' or '\'' or '"' or '?' => (byte)escape,
        _ => null,
    };

    /// <summary>
    /// A <c>\x</c> byte of one or two hex digits; how many digits it used, or null when there are none.
    /// </summary>
    private static int? ReadHexByte(string text, int at, List<byte> bytes)
    {
        var digits = Digits(text, at, 2, 16);
        if (digits == 0) return null;

        bytes.Add((byte)Convert.ToInt32(text.Substring(at, digits), 16));
        return digits;
    }

    /// <summary>
    /// A <c>\u</c> or <c>\U</c> code point of exactly <paramref name="count"/> hex digits, as UTF-8; null when it is short or no
    /// Unicode scalar value.
    /// </summary>
    private static int? ReadCodePoint(string text, int at, int count, List<byte> bytes)
    {
        if (Digits(text, at, count, 16) != count) return null;

        // Eight hex digits can exceed int: read as long, so FFFFFFFF is out of range rather than -1.
        var code = Convert.ToInt64(text.Substring(at, count), 16);
        if (code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) return null;

        bytes.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32((int)code)));
        return count;
    }

    /// <summary>
    /// An octal byte of one to three digits starting at <paramref name="at"/>; how many digits it used, or null past 0xFF.
    /// </summary>
    private static int? ReadOctalByte(string text, int at, List<byte> bytes)
    {
        var digits = Digits(text, at, 3, 8);
        var value = Convert.ToInt32(text.Substring(at, digits), 8);
        if (value > 0xFF) return null;

        bytes.Add((byte)value);
        return digits;
    }

    /// <summary>
    /// How many digits of the base (at most <paramref name="max"/>) start at <paramref name="at"/>.
    /// </summary>
    private static int Digits(string text, int at, int max, int radix)
    {
        var n = 0;
        while (n < max && at + n < text.Length && IsDigit(text[at + n], radix))
        {
            n++;
        }

        return n;
    }

    /// <summary>
    /// Whether the character is a digit of the base (8 or 16).
    /// </summary>
    private static bool IsDigit(char ch, int radix) => radix == 8
        ? ch is >= '0' and <= '7'
        : char.IsAsciiHexDigit(ch);

    /// <summary>
    /// A conversation id is a GUID; anything else could name a file outside the annotations folder.
    /// </summary>
    private static bool IsConversationId(string id) => Guid.TryParse(id, out _);

    /// <summary>
    /// The top-level title field up to its opening quote. agy writes the title first; a field of a nested message
    /// (<c>{...}</c>) is never named title, and a longer name ending in "title" is not matched.
    /// </summary>
    [GeneratedRegex(@"(?<![\w.])title\s*:\s*""", RegexOptions.CultureInvariant)]
    private static partial Regex TitleField();
}
