using Nomad.Core.Code;

namespace Nomad.Core.Tests;

public class TextSearchTests
{
    [Fact]
    public async Task FindsEveryMatchInFileThenLineOrderAndSkipsBinaryFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "nomad-find-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "b.cs"), "var widget = 1;\r\n// no\r\nWidget w; widget();\r\n");
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "a widgets list\n");
            await File.WriteAllBytesAsync(Path.Combine(root, "c.bin"), [119, 105, 100, 103, 101, 116, 0, 1, 2]);

            var hits = await TextSearch.SearchAsync(root, TextSearch.Pattern("widget", matchCase: false, wholeWord: false, regex: false), 100);
            Assert.Equal(
                [("a.txt", 0, 2), ("b.cs", 0, 4), ("b.cs", 2, 0), ("b.cs", 2, 10)],
                hits.Select(h => (Path.GetFileName(h.Path), h.Line, h.Column)).ToList());
            Assert.Equal("Widget w; widget();", hits[2].Text);

            var words = await TextSearch.SearchAsync(root, TextSearch.Pattern("widget", matchCase: true, wholeWord: true, regex: false), 100);
            Assert.Equal([(0, 4), (2, 10)], words.Select(h => (h.Line, h.Column)).ToList());

            var regex = await TextSearch.SearchAsync(root, TextSearch.Pattern(@"w\w+\(", matchCase: false, wholeWord: false, regex: true), 100);
            Assert.Equal("widget(", regex.Single().Text.Substring(regex.Single().Column, regex.Single().Length));

            Assert.ThrowsAny<ArgumentException>(() => TextSearch.Pattern("(", false, false, regex: true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
