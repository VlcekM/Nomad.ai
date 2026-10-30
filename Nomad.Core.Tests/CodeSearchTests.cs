using Nomad.Core.Code;

namespace Nomad.Core.Tests;

public class CodeSearchTests
{
    private const string CSharp = """
        using System;

        namespace Shop.Orders
        {
            /// <summary>An order. { not a brace }</summary>
            [Serializable]
            public sealed partial class OrderBook : IDisposable
            {
                private const int MaxLines = 10;
                private readonly Dictionary<string, List<int>> _lines = new();
                public event Action? Changed;

                public OrderBook(int capacity) { }

                public string Name { get; set; } = "{";

                public int Count => _lines.Count;

                public async Task<bool> SaveAsync<T>(T item, CancellationToken cancel)
                {
                    var local = 1;
                    void Inner() { }
                    return true;
                }

                public enum State { Open, Closed = 2 }

                private record struct Line(int Qty);
            }

            public delegate void Handler(object sender);

            public interface IStore
            {
                void Put(string key);
            }
        }
        """;

    [Fact]
    public void CSharpTypesAndMembersAreFoundWithTheirContainers()
    {
        var symbols = SymbolScanner.Scan("Book.cs", CSharp);
        string Describe(CodeSymbol s) => $"{s.Kind} {s.Container}.{s.Name}" + (s.Type is null ? "" : $" : {s.Type}");

        Assert.Equal(
        [
            "Class Shop.Orders.OrderBook",
            "Constant Shop.Orders.OrderBook.MaxLines : int",
            "Field Shop.Orders.OrderBook._lines : Dictionary<string, List<int>>",
            "Event Shop.Orders.OrderBook.Changed : Action?",
            "Constructor Shop.Orders.OrderBook.OrderBook",
            "Property Shop.Orders.OrderBook.Name : string",
            "Property Shop.Orders.OrderBook.Count : int",
            "Method Shop.Orders.OrderBook.SaveAsync : Task<bool>",
            "Enum Shop.Orders.OrderBook.State",
            "EnumMember Shop.Orders.OrderBook.State.Open",
            "EnumMember Shop.Orders.OrderBook.State.Closed",
            "Struct Shop.Orders.OrderBook.Line",
            "Delegate Shop.Orders.Handler : void",
            "Interface Shop.Orders.IStore",
            "Method Shop.Orders.IStore.Put : void",
        ], symbols.Select(Describe));

        var save = symbols.Single(s => s.Name == "SaveAsync");
        Assert.Equal((18, CSharp.Split('\n')[18].IndexOf("SaveAsync", StringComparison.Ordinal)), (save.Line, save.Column));
    }

    [Fact]
    public void FileScopedNamespacesAndScriptsAndPython()
    {
        var cs = SymbolScanner.Scan("a.cs", "namespace A.B;\n\npublic record Point(int X, int Y);\npublic static class Tools\n{\n    public static int Twice(int x) => x * 2;\n}\n");
        Assert.Equal(["Record A.B.Point", "Class A.B.Tools", "Method A.B.Tools.Twice"], cs.Select(s => $"{s.Kind} {s.Container}.{s.Name}"));

        var ts = SymbolScanner.Scan("app.ts", "export class Store {\n  constructor() {\n  }\n  async load(id: string) {\n    if (id) {\n    }\n  }\n}\nexport const add = (a: number) => a;\nexport interface Item { }\nfunction helper() {}\n");
        Assert.Equal(["Class Store", "Constructor constructor", "Method load", "Function add", "Interface Item", "Function helper"], ts.Select(s => $"{s.Kind} {s.Name}"));

        var py = SymbolScanner.Scan("tools.py", "class Shape:\n    def area(self):\n        pass\n\ndef main():\n    pass\n");
        Assert.Equal(["Class tools.Shape", "Method tools.Shape.area", "Function tools.main"], py.Select(s => $"{s.Kind} {s.Container}.{s.Name}"));
    }

    [Theory]
    [InlineData("ClientHello", "clienthello", true)]
    [InlineData("ClientHello", "Cli", true)]
    [InlineData("ClientHello", "CH", true)]
    [InlineData("ClientHello", "cliHel", true)]
    [InlineData("hello_request", "hr", true)]
    [InlineData("SslClientHelloInfo", "hello", true)]
    [InlineData("ClientHello", "xyz", false)]
    [InlineData("ClientHello", "ClientHelloWorld", false)]
    public void NamesMatchByPrefixWordStartsOrAnywhere(string name, string query, bool matches)
    {
        Assert.Equal(matches, FuzzyMatch.TryMatch(name, query, out _, out _));
    }

    [Fact]
    public void BetterMatchesScoreHigherAndShowWhichCharactersMatched()
    {
        FuzzyMatch.TryMatch("Hello", "hello", out var exact, out _);
        FuzzyMatch.TryMatch("HelloWorld", "hello", out var prefix, out _);
        FuzzyMatch.TryMatch("HttpEndpoint", "he", out var humps, out var humpPositions);
        FuzzyMatch.TryMatch("Othello", "hello", out var inside, out var insidePositions);

        Assert.True(exact > prefix && prefix > humps && humps > inside);
        Assert.Equal([0, 4], humpPositions);
        Assert.Equal([2, 3, 4, 5, 6], insidePositions);
    }
}
