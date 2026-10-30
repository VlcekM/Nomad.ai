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

        var references = await server.ReferencesAsync(file, line, column);
        Assert.True(references.Count > 2, $"{references.Count} references");
        Assert.Contains(references, r => !string.Equals(r.Path, file, StringComparison.OrdinalIgnoreCase));

        var prepared = await server.PrepareRenameAsync(file, line, column);
        Assert.NotNull(prepared);
        var rename = await server.RenameAsync(file, line, column, "IsFolder");
        Assert.True(rename.Count > 1, $"{rename.Count} files");
        Assert.Contains("=> node.IsFolder", LspTextEdit.Apply(string.Join("\n", lines), rename[file]), StringComparison.Ordinal);

        // An error: a name that doesn't exist.
        var broken = string.Join("\n", lines).Replace("=> node.IsDirectory", "=> node.IsDirectoryy", StringComparison.Ordinal);
        await server.ChangeAsync(file, broken);
        IReadOnlyList<LspDiagnostic> diagnostics = [];
        for (var i = 0; i < 20 && !diagnostics.Any(d => d.Severity == LspSeverity.Error); i++)
        {
            diagnostics = await server.DiagnosticsAsync(file);
            Console.WriteLine($"diagnostics: {string.Join(" | ", diagnostics.Select(d => $"{d.Severity} {d.Code} {d.Line}:{d.Column} {d.Message}"))}");
            await Task.Delay(500);
        }

        Assert.Contains(diagnostics, d => d.Severity == LspSeverity.Error && d.Line == line && d.Message.Contains("IsDirectoryy", StringComparison.Ordinal));

        var completions = await server.CompletionAsync(file, line, column, ".");
        Console.WriteLine($"completions: {completions.Items.Count}, incomplete {completions.IsIncomplete}: {string.Join(", ", completions.Items.Take(8).Select(c => $"{c.Label}/{c.Kind}/{c.Edit}"))}");
        var isDirectory = Assert.Single(completions.Items, c => c.Label == "IsDirectory");
        var resolved = await server.ResolveAsync(isDirectory);
        Console.WriteLine($"resolved: {resolved.Detail} / {resolved.Documentation}");

        // A type from a namespace not imported: it brings its using.
        var top = Array.FindIndex(lines, l => l.Contains("=> node.IsDirectory", StringComparison.Ordinal));
        var withType = broken.Split('\n').ToList();
        withType.Insert(top, "        var sb = new StringBuild");
        await server.ChangeAsync(file, string.Join("\n", withType));
        var types = await server.CompletionAsync(file, top, withType[top].Length, null);
        for (var i = 0; i < 10 && !types.Items.Any(c => c.Label.StartsWith("StringBuilder", StringComparison.Ordinal)); i++)
        {
            Console.WriteLine($"types: {types.Items.Count}, incomplete {types.IsIncomplete}");
            await Task.Delay(1000);
            types = await server.CompletionAsync(file, top, withType[top].Length, null);
        }

        if (types.Items.FirstOrDefault(c => c.Label.StartsWith("StringBuilder", StringComparison.Ordinal)) is { } builder)
        {
            var more = await server.ResolveAsync(builder);
            Console.WriteLine($"StringBuilder: {builder.Label} {builder.Edit} -> {string.Join(" | ", more.AdditionalEdits)}");
        }

        // Signature help inside a call: TryGetValue(path, out var at), at its second argument.
        await server.ChangeAsync(file, string.Join("\n", lines));
        var call = lines[get].IndexOf("TryGetValue(", StringComparison.Ordinal) + "TryGetValue(".Length;
        var second = lines[get].IndexOf(", ", call, StringComparison.Ordinal) + 2;
        var help = await server.SignatureHelpAsync(file, get, second, ",", false);
        Assert.NotNull(help);
        var shown = help.Signatures[help.ActiveSignature];
        Console.WriteLine($"signature: {shown.Label} [{string.Join(", ", shown.Parameters.Select(p => shown.Label[p.Start..p.End]))}] active {shown.ActiveParameter ?? help.ActiveParameter}");
        Assert.Contains("TryGetValue", shown.Label, StringComparison.Ordinal);
        Assert.Equal(1, shown.ActiveParameter ?? help.ActiveParameter);
        Assert.Contains("value", shown.Label[shown.Parameters[1].Start..shown.Parameters[1].End], StringComparison.Ordinal);
    }
}

public class LspTextEditTests
{
    [Fact]
    public void EditsApplyAgainstTheOriginalText()
    {
        var text = "one two\r\nthree four\r\nfive";
        var result = LspTextEdit.Apply(text, [new(0, 4, 0, 7, "2"), new(1, 0, 1, 5, "3"), new(2, 4, 2, 4, "!"), new(1, 6, 1, 99, "4")]);
        Assert.Equal("one 2\r\n3 4\r\nfive!", result);
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

public class ChangeDifferenceTests
{
    [Theory]
    [InlineData("abc\r\ndef", "abc\r\nXdef")]
    [InlineData("abc\r\ndef", "abc\r\n\r\ndef")]
    [InlineData("abc\r\ndef", "abcdef")]
    [InlineData("aaaa", "aa")]
    [InlineData("", "x")]
    [InlineData("same", "same")]
    public void TheChangedSpanRebuildsTheText(string old, string text)
    {
        var (start, end, insert) = RoslynServer.Difference(old, text);
        Assert.Equal(text, old[..start] + insert + old[end..]);
        Assert.False(start > 0 && start < old.Length && old[start - 1] == '\r' && old[start] == '\n');
        Assert.False(end > 0 && end < old.Length && old[end - 1] == '\r' && old[end] == '\n');
    }
}

public class CompletionFilterTests
{
    private static LspCompletion Item(string label, string sort = "0", bool preselect = false) => new(label, 7, label, sort, label, null, null, preselect, "{}");

    [Fact]
    public void PrefixBeatsCapitalsBeatsContains()
    {
        var items = new[] { Item("SubBuilder", "3"), Item("StringBuilder", "2"), Item("Builder", "1"), Item("stringy", "0"), Item("Other", "4") };
        var ranked = CompletionFilter.Rank(items, "SB");
        Assert.Equal(["StringBuilder", "SubBuilder"], ranked.Select(r => r.Item.Label));

        ranked = CompletionFilter.Rank(items, "Str");
        Assert.Equal(["StringBuilder", "stringy"], ranked.Select(r => r.Item.Label));
        Assert.Equal([0, 1, 2], ranked[0].Matched);

        ranked = CompletionFilter.Rank(items, "uild");
        Assert.Equal(["Builder", "StringBuilder", "SubBuilder"], ranked.Select(r => r.Item.Label));

        Assert.Equal([0, 6, 7], CompletionFilter.Rank([Item("StringBuilder")], "SBu")[0].Matched);
    }

    [Fact]
    public void NothingTypedKeepsTheServersOrderAndPreselection()
    {
        var items = new[] { Item("b", "1"), Item("a", "2", preselect: true) };
        var ranked = CompletionFilter.Rank(items, "");
        Assert.Equal(["b", "a"], ranked.Select(r => r.Item.Label));
        Assert.Equal(1, CompletionFilter.Best(ranked, ""));
    }
}
