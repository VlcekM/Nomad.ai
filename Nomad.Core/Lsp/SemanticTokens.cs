namespace Nomad.Core.Lsp;

/// <summary>A name the language server classified, 0-based, on one line (columns in UTF-16 units).</summary>
public readonly record struct LspToken(int Line, int Column, int Length);

/// <summary>
/// The protocol's semantic tokens: five numbers a token, each place relative to the one before (lines apart, then the
/// column apart on the same line or from the line's start on a new one), its length, its type (an index into the
/// server's legend) and its modifiers.
/// </summary>
public static class SemanticTokens
{
    /// <summary>The tokens whose type is one of <paramref name="types"/>, in order.</summary>
    public static List<LspToken> Decode(IReadOnlyList<int> data, IReadOnlySet<int> types)
    {
        var tokens = new List<LspToken>();
        int line = 0, column = 0;
        for (var i = 0; i + 4 < data.Count; i += 5)
        {
            var lines = data[i];
            line += lines;
            column = lines == 0 ? column + data[i + 1] : data[i + 1];
            if (types.Contains(data[i + 3]))
            {
                tokens.Add(new LspToken(line, column, data[i + 2]));
            }
        }

        return tokens;
    }

    /// <summary>The legend's types that name a struct: the protocol's "struct" and Roslyn's own (a record struct's).</summary>
    public static HashSet<int> StructTypes(IReadOnlyList<string> legend) =>
        [.. legend.Select((name, i) => (name, i)).Where(t => t.name.Contains("struct", StringComparison.OrdinalIgnoreCase)).Select(t => t.i)];
}
