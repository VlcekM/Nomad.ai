using Nomad.Core.Lsp;

namespace Nomad.Core.Tests;

public class SemanticTokensTests
{
    [Fact]
    public void TypeKindsAreTheLegendsStructInterfaceAndEnumNames()
    {
        var kinds = SemanticTokens.TypeKinds(["class", "struct", "enum", "recordStruct", "interface", "enumMember"]);

        Assert.Equal(
            new Dictionary<int, LspTypeKind> { [1] = LspTypeKind.Struct, [2] = LspTypeKind.Enum, [3] = LspTypeKind.Struct, [4] = LspTypeKind.Interface },
            kinds);
    }

    [Fact]
    public void RelativePlacesAddUpAndOnlyTheAskedTypesCome()
    {
        int[] data =
        [
            2, 4, 5, 1, 0,  // line 2, column 4: a struct
            0, 10, 3, 0, 0, // same line, column 14: a class
            0, 6, 8, 2, 0,  // same line, column 20: an interface
            3, 2, 4, 1, 0,  // line 5, column 2 (from the line's start): a struct
        ];

        Assert.Equal(
        [
            new LspToken(2, 4, 5, LspTypeKind.Struct),
            new LspToken(2, 20, 8, LspTypeKind.Interface),
            new LspToken(5, 2, 4, LspTypeKind.Struct),
        ], SemanticTokens.Decode(data, new Dictionary<int, LspTypeKind> { [1] = LspTypeKind.Struct, [2] = LspTypeKind.Interface }));
    }
}
