using Nomad.Core.Lsp;

namespace Nomad.Core.Tests;

public class RoslynServerTests
{
    [Fact]
    public void FindsTheSolutionNamedLikeItsFolderFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "nomad-sln-" + Guid.NewGuid().ToString("N"), "App");
        Directory.CreateDirectory(Path.Combine(root, "src", "Lib"));
        try
        {
            File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.csproj"), "<Project />");
            var projects = RoslynServer.FindWork(root)!.Value;
            Assert.Null(projects.Solution);
            Assert.Equal([Path.Combine(root, "src", "Lib", "Lib.csproj")], projects.Projects);

            File.WriteAllText(Path.Combine(root, "Other.sln"), "");
            File.WriteAllText(Path.Combine(root, "App.slnx"), "");
            Assert.Equal(Path.Combine(root, "App.slnx"), RoslynServer.FindWork(root)!.Value.Solution);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public void FileUrisRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "a b#c", "File.cs");
        Assert.Equal(path, RoslynServer.PathOf(RoslynServer.Uri(path)));
        Assert.Equal(@"R:\x\y.cs", RoslynServer.PathOf("file:///r%3A/x/y.cs")?.Replace("r:", "R:"));
    }

    // Downloads and runs the real server on this repository: only when NOMAD_ROSLYN_TEST is set (slow, needs the network).
    [Fact]
    public async Task AnswersDefinitionAndHoverOnThisRepository()
    {
        if (Environment.GetEnvironmentVariable("NOMAD_ROSLYN_TEST") is null)
        {
            return;
        }

        if (!RoslynServer.IsInstalled)
        {
            await RoslynServer.InstallAsync();
        }

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AGENTS.md")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        await using var server = await RoslynServer.StartAsync(root);
        await server.Loaded.WaitAsync(TimeSpan.FromMinutes(4));

        var file = Path.Combine(root, "Nomad.App", "Ide", "FileTree.cs");
        var lines = await File.ReadAllLinesAsync(file);
        await server.OpenAsync(file, string.Join("\n", lines));
        var line = Array.FindIndex(lines, l => l.Contains("=> node.IsDirectory", StringComparison.Ordinal));
        var column = lines[line].IndexOf("IsDirectory", StringComparison.Ordinal);

        var definition = Assert.Single(await server.DefinitionAsync(file, line, column));
        Assert.Equal(file, definition.Path, ignoreCase: true);
        Assert.Contains("bool IsDirectory", lines[definition.Line], StringComparison.Ordinal);

        var hover = await server.HoverAsync(file, line, column);
        Assert.Contains("IsDirectory", hover);

        // A library's member: a decompiled file of its own.
        var get = Array.FindIndex(lines, l => l.Contains("OwnWrites.TryGetValue", StringComparison.Ordinal));
        var library = await server.DefinitionAsync(file, get, lines[get].IndexOf("TryGetValue", StringComparison.Ordinal));
        Assert.NotEmpty(library);
        Console.WriteLine($"TryGetValue -> {library[0]}");
    }
}

public class HoverTextTests
{
    [Fact]
    public void HoverMarkdownBecomesQuickInfo()
    {
        var (info, kind) = Nomad.Core.Lsp.HoverText.Parse("```csharp\n(field) int FileTree.\\_count\n```\n  \nHow many \\*files\\* there are, see [`FileTree`](x).")!.Value;
        Assert.Equal((Nomad.Core.Code.SymbolKind.Field, "int FileTree._count", "How many *files* there are, see FileTree."), (kind, info.Signature, info.Summary));

        Assert.Equal(Nomad.Core.Code.SymbolKind.Method, Nomad.Core.Lsp.HoverText.Parse("```csharp\nvoid FileNode.Open(int line)\n```")!.Value.Kind);
        Assert.Equal(Nomad.Core.Code.SymbolKind.Class, Nomad.Core.Lsp.HoverText.Parse("```csharp\nclass Nomad.App.Ide.FileTree\n```")!.Value.Kind);
        Assert.Null(Nomad.Core.Lsp.HoverText.Parse(" "));
    }
}
