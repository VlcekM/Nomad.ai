namespace Nomad.Core.Lsp;

/// <summary>The kinds of type Visual Studio colors apart from classes.</summary>
public enum LspTypeKind
{
    Struct,
    Interface,
    Enum,
}

/// <summary>A type's name the language server classified, 0-based, on one line (columns in UTF-16 units).</summary>
public readonly record struct LspToken(int Line, int Column, int Length, LspTypeKind Kind);

/// <summary>
/// The protocol's semantic tokens: five numbers a token, each place relative to the one before (lines apart, then the
/// column apart on the same line or from the line's start on a new one), its length, its type (an index into the
/// server's legend) and its modifiers.
/// </summary>
public static class SemanticTokens
{
    /// <summary>The tokens whose type is one of <paramref name="types"/>, in order, with its kind.</summary>
    public static List<LspToken> Decode(IReadOnlyList<int> data, IReadOnlyDictionary<int, LspTypeKind> types)
    {
        var tokens = new List<LspToken>();
        int line = 0, column = 0;
        for (var i = 0; i + 4 < data.Count; i += 5)
        {
            var lines = data[i];
            line += lines;
            column = lines == 0 ? column + data[i + 1] : data[i + 1];
            if (types.TryGetValue(data[i + 3], out var kind))
            {
                tokens.Add(new LspToken(line, column, data[i + 2], kind));
            }
        }

        return tokens;
    }

    /// <summary>
    /// The legend's types that name a struct, an interface or an enum: the protocol's names and Roslyn's own (a record
    /// struct's), but not an enum's members.
    /// </summary>
    public static Dictionary<int, LspTypeKind> TypeKinds(IReadOnlyList<string> legend)
    {
        var kinds = new Dictionary<int, LspTypeKind>();
        for (var i = 0; i < legend.Count; i++)
        {
            var name = legend[i];
            if (name.Contains("struct", StringComparison.OrdinalIgnoreCase))
            {
                kinds[i] = LspTypeKind.Struct;
            }
            else if (name.Contains("interface", StringComparison.OrdinalIgnoreCase))
            {
                kinds[i] = LspTypeKind.Interface;
            }
            else if (name.Contains("enum", StringComparison.OrdinalIgnoreCase) && !name.Contains("member", StringComparison.OrdinalIgnoreCase))
            {
                kinds[i] = LspTypeKind.Enum;
            }
        }

        return kinds;
    }
}
