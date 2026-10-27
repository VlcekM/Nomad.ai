using Nomad.Core.Text;

namespace Nomad.Core.Tests;

public class MarkdownTextTests
{
    private static string Apply(string text, TextEdit edit) =>
        text[..edit.Start] + edit.Insert + text[(edit.Start + edit.Length)..];

    [Theory]
    [InlineData("- one", "\n- ")]
    [InlineData("* one", "\n* ")]
    [InlineData("  + one", "\n  + ")]
    [InlineData("1. one", "\n2. ")]
    [InlineData("9) one", "\n10) ")]
    [InlineData("- [x] done", "\n- [ ] ")]
    public void ANewLineInAnItemStartsTheNextOne(string line, string added)
    {
        var edit = MarkdownLists.NewLine(line, line.Length, "\n")!.Value;

        Assert.Equal(line + added, Apply(line, edit));
        Assert.Equal((line + added).Length, edit.SelectionEnd);
    }

    [Fact]
    public void ANewLineInsideANumberedListRenumbersTheRest()
    {
        var text = "1. a\r\n2. b\r\n3. c";
        var edit = MarkdownLists.NewLine(text, "1. a".Length, "\r\n")!.Value;

        Assert.Equal("1. a\r\n2. \r\n3. b\r\n4. c", Apply(text, edit));
        Assert.Equal("1. a\r\n2. ".Length, edit.SelectionEnd);
    }

    [Fact]
    public void ANewLineInAnEmptyItemEndsTheList()
    {
        var text = "- a\n- ";
        var edit = MarkdownLists.NewLine(text, text.Length, "\n")!.Value;

        Assert.Equal("- a\n", Apply(text, edit));
    }

    [Fact]
    public void ANewLineInAnEmptyNestedItemStepsOut()
    {
        var text = "1. a\n   1. b\n   2. ";
        var edit = MarkdownLists.NewLine(text, text.Length, "\n")!.Value;

        Assert.Equal("1. a\n   1. b\n2. ", Apply(text, edit));
        Assert.Equal(Apply(text, edit).Length, edit.SelectionEnd);
    }

    [Fact]
    public void ANewLineOutsideAListIsLeftAlone()
    {
        Assert.Null(MarkdownLists.NewLine("hello", 5, "\n"));
        Assert.Null(MarkdownLists.NewLine("-5 degrees", 10, "\n"));
    }

    [Fact]
    public void TabNestsUnderTheItemAboveAndShiftTabTakesItBack()
    {
        var text = "1. a\n2. b";
        var nested = MarkdownLists.Indent(text, text.Length, text.Length, outdent: false)!.Value;
        var inner = Apply(text, nested);
        Assert.Equal("1. a\n   1. b", inner);

        var back = MarkdownLists.Indent(inner, inner.Length, inner.Length, outdent: true)!.Value;
        Assert.Equal("1. a\n2. b", Apply(inner, back));
    }

    [Fact]
    public void TabOnTextThatIsNoListDoesNothing()
    {
        Assert.Null(MarkdownLists.Indent("plain", 2, 2, outdent: false));
    }

    [Fact]
    public void BackspaceAfterAMarkerRemovesIt()
    {
        var text = "- a\n- b";
        var edit = MarkdownLists.Backspace(text, "- a\n- ".Length)!.Value;

        Assert.Equal("- a\nb", Apply(text, edit));
        Assert.Equal("- a\n".Length, edit.SelectionEnd);
        Assert.Null(MarkdownLists.Backspace(text, text.Length));
    }

    [Fact]
    public void HighlightingMarksSyntaxAndStyles()
    {
        var spans = MarkdownSpans.Highlight("# Title\n- **bold** and `code`\n1. x_y_z");

        MarkdownStyle At(int i) => spans.FirstOrDefault(s => i >= s.Start && i < s.Start + s.Length).Style;
        Assert.Equal(MarkdownStyle.Marker, At(0));
        Assert.True(At(2).HasFlag(MarkdownStyle.Heading));
        Assert.Equal(MarkdownStyle.Bullet, At(8));
        Assert.Equal(MarkdownStyle.Marker, At(10));
        Assert.Equal(MarkdownStyle.Bold, At(12));
        Assert.Equal(MarkdownStyle.Code, At("# Title\n- **bold** and `".Length));
        Assert.Equal(MarkdownStyle.Number, At("# Title\n- **bold** and `code`\n".Length));
        Assert.Equal(MarkdownStyle.None, At("# Title\n- **bold** and `code`\n1. x_".Length));
    }

    [Fact]
    public void FencedCodeIsCodeUntilItCloses()
    {
        var text = "```\n**not bold**\n```\n*it*";
        var spans = MarkdownSpans.Highlight(text);

        MarkdownStyle At(int i) => spans.FirstOrDefault(s => i >= s.Start && i < s.Start + s.Length).Style;
        Assert.Equal(MarkdownStyle.CodeBlock, At(6));
        Assert.Equal(MarkdownStyle.Italic, At(text.Length - 2));
    }
}
