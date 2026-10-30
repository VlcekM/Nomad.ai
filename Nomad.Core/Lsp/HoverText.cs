using System.Text.RegularExpressions;
using Nomad.Core.Code;

namespace Nomad.Core.Lsp;

/// <summary>
/// A language server's hover (markdown: the declaration in a code block, then its documentation) as the editor's
/// Quick Info: the declaration, what sort of symbol it is (Roslyn says "(field)", "(parameter)"…, or the declaration
/// shows it), and the documentation as plain text.
/// </summary>
public static partial class HoverText
{
    public static (QuickInfo Info, SymbolKind Kind)? Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var fence = Fence().Match(markdown);
        string signature;
        string rest;
        if (fence.Success)
        {
            signature = Squeeze(Escape().Replace(fence.Groups["code"].Value, "$1"));
            rest = markdown.Remove(fence.Index, fence.Length);
        }
        else
        {
            // No code block: the first line is the declaration.
            var lines = markdown.Split('\n', 2);
            signature = Squeeze(Plain(lines[0]));
            rest = lines.Length > 1 ? lines[1] : "";
        }

        var kind = KindOf(ref signature);
        var summary = string.Join("\n", Plain(rest).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return (new QuickInfo(signature, summary.Length > 0 ? summary : null), kind);
    }

    // "(field) int Foo._x" -> Field, without its label; else from the declaration's shape.
    private static SymbolKind KindOf(ref string signature)
    {
        if (Label().Match(signature) is { Success: true } label)
        {
            signature = signature[label.Length..];
            return label.Groups[1].Value switch
            {
                "field" => SymbolKind.Field,
                "constant" => SymbolKind.Constant,
                "property" => SymbolKind.Property,
                "event" => SymbolKind.Event,
                "enum member" or "enum value" => SymbolKind.EnumMember,
                "extension" or "method" => SymbolKind.Method,
                "constructor" => SymbolKind.Constructor,
                _ => SymbolKind.Variable, // local variable, parameter, range variable, label
            };
        }

        var words = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words.Take(4))
        {
            switch (word)
            {
                case "class": return SymbolKind.Class;
                case "record": return SymbolKind.Record;
                case "struct": return SymbolKind.Struct;
                case "interface": return SymbolKind.Interface;
                case "enum": return SymbolKind.Enum;
                case "delegate": return SymbolKind.Delegate;
                case "namespace": return SymbolKind.File;
            }
        }

        return signature.Contains('(') ? SymbolKind.Method : signature.Contains("{ get", StringComparison.Ordinal) ? SymbolKind.Property : SymbolKind.Field;
    }

    // Markdown to plain text: escapes, emphasis and inline code marks go, links keep their text.
    private static string Plain(string text)
    {
        text = Link().Replace(text, "$1");
        text = Escape().Replace(text, "$1");
        text = text.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
        text = text.Replace("&nbsp;", " ", StringComparison.Ordinal);
        return System.Net.WebUtility.HtmlDecode(text);
    }

    private static string Squeeze(string text) => Spaces().Replace(text.Trim(), " ");

    [GeneratedRegex(@"```[\w#+-]*\r?\n(?<code>[\s\S]*?)\r?\n?```")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\((local variable|parameter|field|constant|property|event|enum member|enum value|method|extension|constructor|range variable|label|local function|type parameter)\)\s*")]
    private static partial Regex Label();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!<>|])")]
    private static partial Regex Escape();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
