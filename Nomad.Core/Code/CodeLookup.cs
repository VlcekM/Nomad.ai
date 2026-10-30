using System.Text;
using System.Text.RegularExpressions;

namespace Nomad.Core.Code;

/// <summary>
/// A name in the code, where it is used: its text, what comes before its dot (<c>"this"</c>, a variable, a type; "?"
/// for something else, like a call's result; null with no dot), and the file and 0-based line it is on.
/// </summary>
public sealed record CodeReference(string Name, string? Receiver, string Path, int Line);

/// <summary>What a name in the code is: its declaration's signature (as Visual Studio's Quick Info shows it) and summary.</summary>
public sealed record QuickInfo(string Signature, string? Summary);

/// <summary>
/// Go to definition and Quick Info without a compiler: a name is looked up among the indexed declarations, narrowed by
/// what it is used on (a member of the variable's type, of this type), or found declared nearby as a local or a
/// parameter. A guess, but a good one for the usual code.
/// </summary>
public static partial class CodeLookup
{
    // How far up a local or parameter is looked for (the method it is in, usually).
    private const int LocalReach = 400;

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while", "var", "async", "await", "get", "set", "init", "value", "record",
        "partial", "required", "nameof", "when", "where", "yield", "and", "or", "not", "with", "global", "file", "scoped",
    ];

    private static readonly string[] Modifiers =
    [
        "public ", "private ", "protected ", "internal ", "static ", "readonly ", "sealed ", "partial ", "override ",
        "virtual ", "abstract ", "async ", "new ", "extern ", "unsafe ", "required ", "file ", "volatile ",
    ];

    /// <summary>
    /// The declarations a name most likely means, the best first (several when it can't tell); empty for a keyword or
    /// a name declared nowhere it can see. <paramref name="named"/> gives the indexed declarations with a name;
    /// <paramref name="lines"/> are the lines of the file it is used in.
    /// </summary>
    public static List<CodeSymbol> Resolve(Func<string, IReadOnlyList<CodeSymbol>> named, CodeReference at, IReadOnlyList<string> lines)
    {
        if (Keywords.Contains(at.Name) || at.Line < 0 || at.Line >= lines.Count)
        {
            return [];
        }

        // A local or a parameter (not after a dot, but "this." is a field's).
        if (at.Receiver is null && FindLocal(at.Name, lines, at.Line) is { } local)
        {
            return [new CodeSymbol(at.Name, SymbolKind.Variable, local.Type, "", at.Path, local.Line, local.Column)];
        }

        var found = named(at.Name).Where(s => s.Kind != SymbolKind.File).ToList();
        if (found.Any(s => s.IsType))
        {
            // A constructor shares its type's name: the type is the one meant (and after a dot, a nested type or a member).
            found.RemoveAll(s => s.Kind == SymbolKind.Constructor);
        }

        if (found.Count <= 1)
        {
            return found;
        }

        // On something: a member of its type.
        if (at.Receiver is { } receiver && receiver != "?")
        {
            var type = receiver is "this" or "base" ? TypeAt(named, at, lines) : TypeOf(named, receiver, at, lines);
            if (type is not null && found.Where(s => LastPart(s.Container) == type).ToList() is { Count: > 0 } members)
            {
                return members;
            }
        }

        // Nothing to go on: this type's own first, then this file's, then types before members.
        if (at.Receiver is null or "this" or "base" && TypeAt(named, at, lines) is { } own &&
            found.Where(s => LastPart(s.Container) == own && SamePath(s.Path, at.Path)).ToList() is { Count: > 0 } mine)
        {
            return mine;
        }

        var here = found.Where(s => SamePath(s.Path, at.Path)).ToList();
        if (here.Count > 0)
        {
            return here;
        }

        var types = found.Where(s => s.IsType).ToList();
        return at.Receiver is null && types.Count > 0 ? types : found;
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string LastPart(string container) => container[(container.LastIndexOf('.') + 1)..];

    // The type the line is in: the last type declared above it in the file (by the index, so nesting is right enough).
    private static string? TypeAt(Func<string, IReadOnlyList<CodeSymbol>> named, CodeReference at, IReadOnlyList<string> lines)
    {
        for (var i = Math.Min(at.Line, lines.Count - 1); i >= 0; i--)
        {
            if (TypeDecl().Match(lines[i]) is { Success: true } decl &&
                named(decl.Groups["name"].Value).Any(s => s.IsType && s.Line == i && SamePath(s.Path, at.Path)))
            {
                return decl.Groups["name"].Value;
            }
        }

        return null;
    }

    // What a receiver is: a type (a static member), a local or parameter of a declared type, or a field or property.
    private static string? TypeOf(Func<string, IReadOnlyList<CodeSymbol>> named, string receiver, CodeReference at, IReadOnlyList<string> lines)
    {
        if (named(receiver).Any(s => s.IsType))
        {
            return receiver;
        }

        if (FindLocal(receiver, lines, at.Line) is { Type: { } localType })
        {
            return Simple(localType);
        }

        var own = TypeAt(named, at, lines);
        var members = named(receiver).Where(s => s.Type is not null && !s.IsType).ToList();
        var member = members.FirstOrDefault(s => own is not null && LastPart(s.Container) == own) ?? (members.Count == 1 ? members[0] : null);
        return member?.Type is { } memberType ? Simple(memberType) : null;
    }

    // "List<FileNode>?" -> "List"; "Nomad.Core.GitFiles" -> "GitFiles".
    private static string Simple(string type)
    {
        var t = type.Trim().TrimEnd('?');
        var generic = t.IndexOf('<');
        if (generic > 0)
        {
            t = t[..generic];
        }

        return LastPart(t.TrimEnd('[', ']'));
    }

    /// <summary>A local or parameter declared above a line (in its method): where, and its type (null for <c>var</c> it can't tell).</summary>
    public static (int Line, int Column, string? Type)? FindLocal(string name, IReadOnlyList<string> lines, int line)
    {
        var pattern = new Regex(
            $@"(?:(?<type>[A-Za-z_][\w.]*(?:<[^<>()]*(?:<[^<>()]*>)?[^<>()]*>)?(?:\[\])*\??)\s+|\b(?:out|ref|in)\s+(?<type>var)\s+|\(\s*(?<type>var)\s+)@?{Regex.Escape(name)}\b(?=\s*(?:[=;,)]|\bin\b|$|:(?!:)))",
            RegexOptions.CultureInvariant);
        for (var i = Math.Min(line, lines.Count - 1); i >= Math.Max(0, line - LocalReach); i--)
        {
            var text = lines[i];
            foreach (Match m in pattern.Matches(text))
            {
                var type = m.Groups["type"].Success ? m.Groups["type"].Value : null;
                if (type is null || Keywords.Contains(type) && type is not ("var" or "string" or "int" or "bool" or "long" or "double" or "object" or "char" or "byte" or "float" or "decimal"))
                {
                    continue;
                }

                // A field (declared with modifiers, not in parentheses) is no local: the index has it, with its summary.
                var column = m.Index + m.Length - name.Length;
                if (Depth(text[..m.Index]) == 0 && FieldStart().IsMatch(text))
                {
                    return null;
                }

                if (type == "var")
                {
                    type = NewType().Match(text, m.Index) is { Success: true } created ? created.Groups["type"].Value : null;
                }

                return (i, column, type);
            }

            // The start of the method (or type) it is in: its parameters were the last chance.
            if (MemberStart().IsMatch(text))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>The Quick Info of a declaration: its signature as declared (without modifiers, body or initializer, its type before the name) and its summary.</summary>
    public static QuickInfo Describe(CodeSymbol symbol, IReadOnlyList<string> lines)
    {
        if (symbol.Line < 0 || symbol.Line >= lines.Count)
        {
            return new QuickInfo(symbol.Name, null);
        }

        // The declaration: from its line on, until its parameters close.
        var text = new StringBuilder(lines[symbol.Line].Trim());
        var depth = Depth(text.ToString());
        for (var i = symbol.Line + 1; depth > 0 && i < lines.Count && i < symbol.Line + 8; i++)
        {
            text.Append(' ').Append(lines[i].Trim());
            depth = Depth(text.ToString());
        }

        var signature = text.ToString();
        if (symbol.Kind == SymbolKind.Variable)
        {
            // A local: its type and name (a parameter's or a declaration's).
            var type = symbol.Type is { } t && t != "var" ? t : null;
            signature = type is null ? $"(local) {symbol.Name}" : $"(local) {type} {symbol.Name}";
            return new QuickInfo(signature, null);
        }

        signature = Cut(signature, symbol);
        foreach (var modifier in Modifiers)
        {
            while (signature.StartsWith(modifier, StringComparison.Ordinal))
            {
                signature = signature[modifier.Length..];
            }
        }

        // Members get their type's name before theirs (FileTree.Refresh), types their namespace.
        var nameAt = WordIndex(signature, symbol.Name);
        if (nameAt >= 0 && symbol.Container.Length > 0 && symbol.Kind is not (SymbolKind.Variable or SymbolKind.EnumMember))
        {
            var prefix = symbol.IsType ? symbol.Container : LastPart(symbol.Container);
            signature = signature[..nameAt] + prefix + "." + signature[nameAt..];
        }
        else if (symbol.Kind == SymbolKind.EnumMember && symbol.Container.Length > 0)
        {
            signature = LastPart(symbol.Container) + "." + signature.TrimEnd(',');
        }

        return new QuickInfo(Squeeze(signature), Summary(lines, symbol.Line));
    }

    private static int Depth(string text) => text.Count(c => c == '(') - text.Count(c => c == ')');

    private static int WordIndex(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !(char.IsLetterOrDigit(text[at - 1]) || text[at - 1] is '_' or '.');
            var after = at + word.Length >= text.Length || !(char.IsLetterOrDigit(text[at + word.Length]) || text[at + word.Length] == '_');
            if (before && after)
            {
                return at;
            }
        }

        return -1;
    }

    // Leaves off a body, an initializer or a base list: the declaration up to where its name's part ends.
    private static string Cut(string text, CodeSymbol symbol)
    {
        int Find(string what, int from = 0) => text.IndexOf(what, from, StringComparison.Ordinal) is var i and >= 0 ? i : int.MaxValue;

        var nameAt = Math.Max(0, WordIndex(text, symbol.Name));
        var end = Math.Min(Find("{"), Math.Min(Find("=>", nameAt), Find(";")));
        if (symbol.Kind is SymbolKind.Field or SymbolKind.Constant or SymbolKind.Property or SymbolKind.Event or SymbolKind.EnumMember)
        {
            end = Math.Min(end, Find(" =", nameAt));
        }

        if (symbol.IsType && Find(":", nameAt) is var colon && colon < end)
        {
            end = colon;
        }

        return (end == int.MaxValue ? text : text[..end]).Trim();
    }

    private static string Squeeze(string text) => Spaces().Replace(text, " ");

    /// <summary>The <c>///</c> summary right above a line (past its attributes), as plain text.</summary>
    public static string? Summary(IReadOnlyList<string> lines, int line)
    {
        var i = line - 1;
        while (i >= 0 && lines[i].TrimStart().StartsWith('[') && !lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
        {
            i--;
        }

        var doc = new List<string>();
        for (; i >= 0 && lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal); i--)
        {
            doc.Insert(0, lines[i].TrimStart()[3..].Trim());
        }

        if (doc.Count == 0)
        {
            return null;
        }

        var xml = string.Join(' ', doc);
        var summary = SummaryTag().Match(xml) is { Success: true } m ? m.Groups[1].Value : xml.Contains('<') ? "" : xml;
        summary = Reference().Replace(summary, r => LastPart(r.Groups[2].Value.Replace("T:", "").Replace("M:", "").Replace("P:", "")));
        summary = Tag().Replace(summary, "");
        summary = System.Net.WebUtility.HtmlDecode(Squeeze(summary).Trim());
        return summary.Length > 0 ? summary : null;
    }

    [GeneratedRegex(@"\b(?:class|struct|interface|enum|record(?:\s+struct|\s+class)?)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex TypeDecl();

    [GeneratedRegex(@"\bnew\s+(?<type>[A-Za-z_][\w.]*)")]
    private static partial Regex NewType();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|static|override|virtual|async|abstract|sealed|partial|unsafe|extern|new)\s+)+[^=;]*\(|^\s*(?:(?:public|private|protected|internal|static|sealed|partial|abstract)\s+)*(?:class|struct|interface|record|enum)\b")]
    private static partial Regex MemberStart();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|static|readonly|const|volatile|required|new)\s+)+")]
    private static partial Regex FieldStart();

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryTag();

    [GeneratedRegex(@"<(see|seealso|paramref|typeparamref)\s+(?:cref|name|langword)=""([^""]*)""\s*/>")]
    private static partial Regex Reference();

    [GeneratedRegex(@"</?\w+[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
