using Nomad.Core.Code;

namespace Nomad.Core.Tests;

public class CodeLookupTests
{
    private const string Tree = """
        namespace App;

        /// <summary>A file or folder in the <see cref="FileTree"/>.</summary>
        public sealed class FileNode
        {
            public string Name { get; } = "";

            /// <summary>Opens it &amp; shows it.</summary>
            public void Open(int line,
                int column)
            {
            }
        }

        public sealed class FileTree
        {
            public FileNode Root { get; } = new();

            public string Name => "tree";

            /// <summary>How many.</summary>
            private int _count;

            public int Count => _count;

            public void Open(string path)
            {
                var node = new FileNode();
                node.Open(1, 2);
                Root.Open(3, 4);
                Open(path);
                foreach (var child in Children)
                {
                    child.Name.ToString();
                }
            }
        }
        """;

    private static readonly string[] Lines = Tree.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    private static readonly List<CodeSymbol> Symbols = SymbolScanner.Scan("tree.cs", Tree).ToList();

    private static IReadOnlyList<CodeSymbol> Named(string name) => Symbols.Where(s => s.Name == name).ToList();

    private static int LineOf(string text) => Array.FindIndex(Lines, l => l.Contains(text, StringComparison.Ordinal));

    private static List<CodeSymbol> Resolve(string name, string? receiver, string onLine) =>
        CodeLookup.Resolve(Named, new CodeReference(name, receiver, "tree.cs", LineOf(onLine)), Lines);

    [Fact]
    public void MembersResolveByWhatTheyAreUsedOn()
    {
        // A local of a type: that type's member.
        Assert.Equal("App.FileNode", Assert.Single(Resolve("Open", "node", "node.Open(1, 2)")).Container);

        // A property's type.
        Assert.Equal("App.FileNode", Assert.Single(Resolve("Open", "Root", "Root.Open(3, 4)")).Container);

        // No receiver: this type's own.
        Assert.Equal("App.FileTree", Assert.Single(Resolve("Open", null, "Open(path);")).Container);

        // A type name is the type, not its constructor.
        Assert.Equal(SymbolKind.Class, Assert.Single(Resolve("FileNode", null, "var node = new FileNode()")).Kind);
    }

    [Fact]
    public void LocalsAndParametersAreFoundAboveTheirUse()
    {
        var node = Assert.Single(Resolve("node", null, "node.Open(1, 2)"));
        Assert.Equal((SymbolKind.Variable, LineOf("var node = new"), "FileNode"), (node.Kind, node.Line, node.Type));

        var path = Assert.Single(Resolve("path", null, "Open(path);"));
        Assert.Equal((LineOf("public void Open(string path)"), "string"), (path.Line, path.Type));

        Assert.Equal(LineOf("foreach (var child"), Assert.Single(Resolve("child", null, "child.Name")).Line);
        Assert.Empty(Resolve("return", null, "Open(path);"));

        // A field is the index's, not a local.
        var count = Assert.Single(Resolve("_count", null, "public int Count => _count"));
        Assert.Equal((SymbolKind.Field, LineOf("private int _count")), (count.Kind, count.Line));
        Assert.Equal(new QuickInfo("int FileTree._count", "How many."), CodeLookup.Describe(count, Lines));
    }

    [Fact]
    public void QuickInfoShowsTheSignatureWithItsTypeAndTheSummary()
    {
        var open = Resolve("Open", "node", "node.Open(1, 2)")[0];
        Assert.Equal(new QuickInfo("void FileNode.Open(int line, int column)", "Opens it & shows it."), CodeLookup.Describe(open, Lines));

        var type = Resolve("FileNode", null, "var node = new FileNode()")[0];
        Assert.Equal(new QuickInfo("class App.FileNode", "A file or folder in the FileTree."), CodeLookup.Describe(type, Lines));

        var root = Resolve("Root", null, "Root.Open(3, 4)")[0];
        Assert.Equal("FileNode FileTree.Root", CodeLookup.Describe(root, Lines).Signature);

        Assert.Equal("(local) FileNode node", CodeLookup.Describe(Resolve("node", null, "node.Open(1, 2)")[0], Lines).Signature);
    }
}
