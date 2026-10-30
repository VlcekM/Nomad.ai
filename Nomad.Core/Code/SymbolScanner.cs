using System.Text;
using System.Text.RegularExpressions;

namespace Nomad.Core.Code;

/// <summary>
/// Finds the declarations in a source file without a compiler, line by line: C# types and their members (tracking
/// namespaces, nesting and brace depth, with comments and strings blanked out first), and the top-level shapes of
/// TypeScript/JavaScript and Python. Good enough to jump to things by name, as Visual Studio's Ctrl+T does.
/// </summary>
public static partial class SymbolScanner
{
    /// <summary>The files it reads symbols from (by extension); any other file is found by its name only.</summary>
    public static bool CanScan(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".mts" or ".cts" or ".py";

    public static List<CodeSymbol> Scan(string path, string text) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => ScanCSharp(path, text),
        ".py" => ScanPython(path, text),
        _ => ScanScript(path, text),
    };

    private static string[] Lines(string text) => text.Split('\n');

    // ------------------------------------------------------------------ C#

    private sealed record Frame(string Name, SymbolKind Kind, int BodyDepth, bool IsNamespace, string OuterNamespace);

    private static readonly HashSet<string> NotTypes =
    [
        "return", "using", "namespace", "class", "struct", "interface", "enum", "record", "delegate", "operator",
        "implicit", "explicit", "get", "set", "init", "add", "remove", "case", "goto", "yield", "throw", "await",
        "else", "if", "while", "for", "foreach", "switch", "lock", "do", "try", "catch", "finally", "base", "this",
    ];

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*")]
    private static partial Regex Attributes();

    [GeneratedRegex(@"^namespace\s+(?<name>[\w.]+)\s*(?<semi>;)?")]
    private static partial Regex NamespaceDecl();

    [GeneratedRegex(@"^(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|file|unsafe|new|ref)\s+)*(?<kind>class|struct|interface|enum|record\s+struct|record\s+class|record)\s+(?<name>@?[A-Za-z_]\w*)")]
    private static partial Regex TypeDecl();

    [GeneratedRegex(@"^(?:(?:public|private|protected|internal|static|unsafe|new|file)\s+)*delegate\s+(?<type>.+?)\s+(?<name>@?[A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(")]
    private static partial Regex DelegateDecl();

    [GeneratedRegex(@"^(?<mods>(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|unsafe|new|readonly|volatile|partial|required|const|event|fixed|ref)\s+)*)(?<type>\((?:[^()]*)\)(?:\s*\?)?|[A-Za-z_][\w.]*(?:\s*<[^;=(){}]*?>)?(?:\s*\?)?(?:\s*\[[\s,]*\])*(?:\s*\?)?)\s+(?<name>@?[A-Za-z_]\w*)\s*(?<tail><[^<>()]*>\s*\(|\(|\{|=>|=|;|,|$)")]
    private static partial Regex MemberDecl();

    [GeneratedRegex(@"^(?:(?:public|private|protected|internal|static|extern|unsafe)\s+)*(?<name>@?[A-Za-z_]\w*)\s*\(")]
    private static partial Regex ConstructorDecl();

    [GeneratedRegex(@"^(?<name>@?[A-Za-z_]\w*)\s*(?:=[^,]*)?,?\s*$")]
    private static partial Regex EnumMemberDecl();

    private static List<CodeSymbol> ScanCSharp(string path, string text)
    {
        var symbols = new List<CodeSymbol>();
        var frames = new List<Frame>();
        var cleaner = new CSharpCleaner();
        var ns = "";
        var depth = 0;
        (string Name, SymbolKind Kind, bool IsNamespace)? pending = null;

        string Container()
        {
            var parts = new List<string>();
            if (ns.Length > 0)
            {
                parts.Add(ns);
            }

            parts.AddRange(frames.Where(f => !f.IsNamespace).Select(f => f.Name));
            return string.Join('.', parts);
        }

        // An enum's members, one or more to a line: "Open, Closed = 2,".
        void AddEnumMembers(string segment, int offset, int line)
        {
            foreach (var part in segment.Split(','))
            {
                if (part.Trim().Length > 0 && EnumMemberDecl().Match(part.Trim()) is { Success: true } member)
                {
                    var name = member.Groups["name"].Value;
                    symbols.Add(new CodeSymbol(name.TrimStart('@'), SymbolKind.EnumMember, null, Container(), path, line, offset + part.IndexOf(name, StringComparison.Ordinal)));
                }

                offset += part.Length + 1;
            }
        }

        var lines = Lines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            var clean = cleaner.Clean(lines[i].TrimEnd('\r'));
            var oneLineEnum = -1;
            var attrs = Attributes().Match(clean).Length;
            var code = clean[attrs..];
            var top = frames.Count > 0 ? frames[^1] : null;

            if (NamespaceDecl().Match(code) is { Success: true } nsMatch)
            {
                var name = nsMatch.Groups["name"].Value;
                if (nsMatch.Groups["semi"].Success)
                {
                    ns = name; // File-scoped.
                }
                else
                {
                    pending = (name, SymbolKind.Class, true);
                }
            }
            else if (TypeDecl().Match(code) is { Success: true } type)
            {
                var kindText = type.Groups["kind"].Value;
                var kind = kindText switch
                {
                    "class" => SymbolKind.Class,
                    "struct" => SymbolKind.Struct,
                    "interface" => SymbolKind.Interface,
                    "enum" => SymbolKind.Enum,
                    _ when kindText.EndsWith("struct", StringComparison.Ordinal) => SymbolKind.Struct,
                    _ => SymbolKind.Record,
                };
                var name = type.Groups["name"].Value.TrimStart('@');
                symbols.Add(new CodeSymbol(name, kind, null, Container(), path, i, attrs + type.Groups["name"].Index));
                pending = (name, kind, false);
                if (kind == SymbolKind.Enum && clean.IndexOf('{') is >= 0 and var open)
                {
                    oneLineEnum = open; // Its members on this line too: after the braces push its frame.
                }
            }
            else if (DelegateDecl().Match(code) is { Success: true } del)
            {
                symbols.Add(new CodeSymbol(del.Groups["name"].Value.TrimStart('@'), SymbolKind.Delegate, Tidy(del.Groups["type"].Value), Container(), path, i, attrs + del.Groups["name"].Index));
            }
            else if (top is { IsNamespace: false } && depth == top.BodyDepth && code.Length > 0)
            {
                if (top.Kind == SymbolKind.Enum)
                {
                    AddEnumMembers(code.Split('}')[0], attrs, i);
                }
                else if (ConstructorDecl().Match(code) is { Success: true } ctor && ctor.Groups["name"].Value.TrimStart('@') == top.Name)
                {
                    symbols.Add(new CodeSymbol(top.Name, SymbolKind.Constructor, null, Container(), path, i, attrs + ctor.Groups["name"].Index));
                }
                else if (MemberDecl().Match(code) is { Success: true } member && !NotTypes.Contains(member.Groups["type"].Value))
                {
                    var name = member.Groups["name"].Value.TrimStart('@');
                    var mods = member.Groups["mods"].Value;
                    var tail = member.Groups["tail"].Value;
                    var kind = tail.EndsWith('(')
                        ? SymbolKind.Method
                        : mods.Contains("event", StringComparison.Ordinal) ? SymbolKind.Event
                        : mods.Contains("const", StringComparison.Ordinal) ? SymbolKind.Constant
                        : tail is "{" or "=>" or "" ? SymbolKind.Property
                        : SymbolKind.Field;
                    if (name != "operator")
                    {
                        symbols.Add(new CodeSymbol(name, kind, Tidy(member.Groups["type"].Value), Container(), path, i, attrs + member.Groups["name"].Index));
                    }
                }
            }

            // Braces: a pending type or namespace opens its body at its "{"; a ";" first ends it (a record with no body).
            foreach (var c in clean)
            {
                if (c == '{')
                {
                    depth++;
                    if (pending is { } p)
                    {
                        frames.Add(new Frame(p.Name, p.Kind, depth, p.IsNamespace, ns));
                        if (p.IsNamespace)
                        {
                            ns = ns.Length == 0 ? p.Name : ns + "." + p.Name;
                        }

                        pending = null;
                    }
                }
                else if (c == '}')
                {
                    while (frames.Count > 0 && frames[^1].BodyDepth >= depth)
                    {
                        var closed = frames[^1];
                        frames.RemoveAt(frames.Count - 1);
                        if (closed.IsNamespace)
                        {
                            ns = closed.OuterNamespace;
                        }
                    }

                    depth = Math.Max(0, depth - 1);
                }
                else if (c == ';' && pending is { IsNamespace: false })
                {
                    pending = null;
                }

                if (oneLineEnum >= 0 && c == '{')
                {
                    // "enum State { Open, Closed }": its frame is pushed now, for the members' container.
                    var body = clean[(oneLineEnum + 1)..];
                    var close = body.IndexOf('}');
                    AddEnumMembers(close >= 0 ? body[..close] : body, oneLineEnum + 1, i);
                    oneLineEnum = -1;
                }
            }
        }

        return symbols;
    }

    private static string Tidy(string type) => string.Join(' ', type.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Blanks out comments, strings and character literals (keeping columns), across lines.</summary>
    private sealed class CSharpCleaner
    {
        private bool _inBlock;
        private bool _inVerbatim;
        private bool _inRaw;

        public string Clean(string line)
        {
            var sb = new StringBuilder(line.Length);
            var i = 0;
            while (i < line.Length)
            {
                if (_inBlock)
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    var stop = end < 0 ? line.Length : end + 2;
                    sb.Append(' ', stop - i);
                    i = stop;
                    _inBlock = end < 0;
                    continue;
                }

                if (_inRaw || _inVerbatim)
                {
                    var close = _inRaw ? "\"\"\"" : "\"";
                    var end = line.IndexOf(close, i, StringComparison.Ordinal);
                    while (_inVerbatim && end >= 0 && end + 1 < line.Length && line[end + 1] == '"')
                    {
                        end = line.IndexOf('"', end + 2); // "" inside a verbatim string.
                    }

                    var stop = end < 0 ? line.Length : end + close.Length;
                    sb.Append(' ', stop - i);
                    i = stop;
                    if (end >= 0)
                    {
                        _inRaw = _inVerbatim = false;
                    }

                    continue;
                }

                var c = line[i];
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    sb.Append(' ', line.Length - i);
                    break;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    _inBlock = true;
                    continue;
                }

                if (c == '"' && line.AsSpan(i).StartsWith("\"\"\""))
                {
                    sb.Append("   ");
                    i += 3;
                    _inRaw = true;
                    continue;
                }

                if (c == '"' && i > 0 && (line[i - 1] == '@' || i > 1 && line[i - 1] == '$' && line[i - 2] == '@'))
                {
                    sb.Append(' ');
                    i++;
                    _inVerbatim = true;
                    continue;
                }

                if (c is '"' or '\'')
                {
                    // A regular string or char: to its closing quote on this line.
                    var j = i + 1;
                    while (j < line.Length && line[j] != c)
                    {
                        j += line[j] == '\\' ? 2 : 1;
                    }

                    var stop = Math.Min(line.Length, j + 1);
                    sb.Append(' ', stop - i);
                    i = stop;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ TypeScript / JavaScript

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?<kind>class|interface|enum|type|function\*?|const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)(?<rest>.*)$")]
    private static partial Regex ScriptDecl();

    [GeneratedRegex(@"^(?<indent>\s+)(?:(?:public|private|protected|static|async|readonly|override|get|set)\s+)*(?<name>[A-Za-z_$#][\w$]*)\s*(?:<[^>]*>)?\s*\([^;]*$")]
    private static partial Regex ScriptMethod();

    private static readonly HashSet<string> ScriptKeywords = ["if", "for", "while", "switch", "catch", "function", "return", "with", "await", "typeof", "new", "else", "do", "super", "import"];

    private static List<CodeSymbol> ScanScript(string path, string text)
    {
        var symbols = new List<CodeSymbol>();
        var file = Path.GetFileName(path);
        string? cls = null;
        var lines = Lines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (ScriptDecl().Match(line) is { Success: true } decl)
            {
                var name = decl.Groups["name"].Value;
                var rest = decl.Groups["rest"].Value;
                var indented = line.Length > 0 && char.IsWhiteSpace(line[0]);
                SymbolKind? kind = decl.Groups["kind"].Value switch
                {
                    "class" => SymbolKind.Class,
                    "interface" or "type" => SymbolKind.Interface,
                    "enum" => SymbolKind.Enum,
                    "const" or "let" or "var" when rest.Contains("=>", StringComparison.Ordinal) || rest.Contains("function", StringComparison.Ordinal) => SymbolKind.Function,
                    "const" or "let" or "var" => indented ? null : SymbolKind.Variable,
                    _ => SymbolKind.Function,
                };
                if (kind is { } k)
                {
                    symbols.Add(new CodeSymbol(name, k, null, file, path, i, decl.Groups["name"].Index));
                }

                if (kind == SymbolKind.Class)
                {
                    cls = name;
                }
            }
            else if (cls is not null && ScriptMethod().Match(line) is { Success: true } method && line.TrimEnd().EndsWith('{') &&
                     !ScriptKeywords.Contains(method.Groups["name"].Value))
            {
                var name = method.Groups["name"].Value;
                symbols.Add(new CodeSymbol(name, name == "constructor" ? SymbolKind.Constructor : SymbolKind.Method, null, cls, path, i, method.Groups["name"].Index));
            }
        }

        return symbols;
    }

    // ------------------------------------------------------------------ Python

    [GeneratedRegex(@"^(?<indent>\s*)(?:async\s+)?(?<kind>def|class)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex PythonDecl();

    private static List<CodeSymbol> ScanPython(string path, string text)
    {
        var symbols = new List<CodeSymbol>();
        var module = Path.GetFileNameWithoutExtension(path);
        var classes = new List<(int Indent, string Name)>();
        var lines = Lines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            if (PythonDecl().Match(lines[i]) is not { Success: true } decl)
            {
                continue;
            }

            var indent = decl.Groups["indent"].Value.Replace("\t", "    ").Length;
            classes.RemoveAll(c => c.Indent >= indent);
            var name = decl.Groups["name"].Value;
            var isClass = decl.Groups["kind"].Value == "class";
            var container = classes.Count > 0 ? module + "." + string.Join('.', classes.Select(c => c.Name)) : module;
            symbols.Add(new CodeSymbol(name, isClass ? SymbolKind.Class : classes.Count > 0 ? SymbolKind.Method : SymbolKind.Function, null, container, path, i, decl.Groups["name"].Index));
            if (isClass)
            {
                classes.Add((indent, name));
            }
        }

        return symbols;
    }
}
