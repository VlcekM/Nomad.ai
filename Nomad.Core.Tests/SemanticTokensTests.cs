using Nomad.Core.Lsp;

namespace Nomad.Core.Tests;

public class SemanticTokensTests
{
    [Fact]
    public void StructTypesAreTheLegendsStructNames()
    {
        Assert.Equal([1, 3], SemanticTokens.StructTypes(["class", "struct", "enum", "recordStructName"]).Order());
    }

    [Fact]
    public void RelativePlacesAddUpAndOnlyTheAskedTypesCome()
    {
        int[] data =
        [
            2, 4, 5, 1, 0,  // line 2, column 4: a struct
            0, 10, 3, 0, 0, // same line, column 14: a class
            0, 6, 8, 1, 0,  // same line, column 20: a struct
            3, 2, 4, 1, 0,  // line 5, column 2 (from the line's start): a struct
        ];

        Assert.Equal(
        [
            new LspToken(2, 4, 5),
            new LspToken(2, 20, 8),
            new LspToken(5, 2, 4),
        ], SemanticTokens.Decode(data, new HashSet<int> { 1 }));
    }
}
