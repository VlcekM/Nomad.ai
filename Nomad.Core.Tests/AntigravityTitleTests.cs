using Nomad.Core.Antigravity;

namespace Nomad.Core.Tests;

/// <summary>
/// Reading and writing a Gemini chat's name in Antigravity's own annotation record.
/// </summary>
public sealed class AntigravityTitleTests
{
    private const string Record =
        "title:\"Parser Bug Hunt\"  archived:false  archival_status_timestamp:{seconds:1795536870  nanos:666038300}  " +
        "last_user_view_time:{seconds:1795552375  nanos:273000000}";

    [Fact]
    public void ReadsTheTitleOfARecordAsAgyWritesIt()
    {
        Assert.Equal("Parser Bug Hunt", AntigravityTitles.ParseTitle(Record));
        Assert.Equal("It’s the parser, not the lexer", AntigravityTitles.ParseTitle("title:\"It’s the parser, not the lexer\""));
        Assert.Null(AntigravityTitles.ParseTitle("last_user_view_time:{seconds:1795536872 nanos:160000000}"));
        Assert.Null(AntigravityTitles.ParseTitle("subtitle:\"not this\""));
        Assert.Null(AntigravityTitles.ParseTitle("title:\"never closed"));
    }

    [Fact]
    public void UnescapesTheTextFormatsEscapes()
    {
        Assert.Equal("say \"hi\" \\ there\nnext", AntigravityTitles.ParseTitle(@"title:""say \""hi\"" \\ there\nnext"""));
        Assert.Equal("é", AntigravityTitles.ParseTitle(@"title:""\303\251"""));
        Assert.Equal("é", AntigravityTitles.ParseTitle(@"title:""\xc3\xa9"""));
        Assert.Equal("é😀", AntigravityTitles.ParseTitle(@"title:""é\U0001F600"""));
        Assert.Equal("it's", AntigravityTitles.ParseTitle(@"title:""it\'s"""));
    }

    [Fact]
    public void ACodePointPastUnicodeIsMalformedRatherThanAnError()
    {
        Assert.Null(AntigravityTitles.ParseTitle(@"title:""\UFFFFFFFF"""));
        Assert.Null(AntigravityTitles.ParseTitle(@"title:""\U80000000"""));
        Assert.Null(AntigravityTitles.ParseTitle(@"title:""\U00110000"""));
    }

    [Fact]
    public void ADamagedTitleIsLeftAloneRatherThanDoubled()
    {
        Assert.Null(AntigravityTitles.WithTitle("title:\"never closed  archived:false", "New"));
        Assert.Null(AntigravityTitles.WithTitle(@"title:""bad \q escape""  archived:false", "New"));
    }

    [Fact]
    public void RenamingKeepsEveryOtherField()
    {
        var renamed = AntigravityTitles.WithTitle(Record, "Parser Bug Hunt - Gemini")!;

        Assert.Equal(Record.Replace("Parser Bug Hunt", "Parser Bug Hunt - Gemini"), renamed);
        Assert.Equal("Parser Bug Hunt - Gemini", AntigravityTitles.ParseTitle(renamed));
    }

    [Fact]
    public void ARecordWithoutATitleGetsOneFirstAndAnEmptyOneIsJustTheTitle()
    {
        var viewed = "last_user_view_time:{seconds:1795536872 nanos:160000000}";

        Assert.Equal("title:\"Named\"  " + viewed, AntigravityTitles.WithTitle(viewed, "Named"));
        Assert.Equal("title:\"Named\"", AntigravityTitles.WithTitle("", "Named"));
    }

    [Theory]
    [InlineData("Plain")]
    [InlineData("Quote \" and backslash \\ and tab \t")]
    [InlineData("Bell \a and DEL \u007f and a curly apostrophe’s here 😀")]
    public void WhatIsWrittenReadsBackTheSame(string title) =>
        Assert.Equal(title, AntigravityTitles.ParseTitle(AntigravityTitles.WithTitle(Record, title)!));

    [Fact]
    public void WritesAtomicallyIntoTheDataFolderAndReadsBack()
    {
        var saved = AntigravityTitles.DataFolder;
        var folder = Path.Combine(AppContext.BaseDirectory, $"agy-{Guid.NewGuid():N}");
        var id = Guid.NewGuid().ToString();
        AntigravityTitles.DataFolder = folder;
        try
        {
            Assert.Null(AntigravityTitles.Read(id));
            Directory.CreateDirectory(Path.Combine(folder, "annotations"));
            File.WriteAllText(Path.Combine(folder, "annotations", id + ".pbtxt"), Record);

            Assert.Equal("Parser Bug Hunt", AntigravityTitles.Read(id));
            Assert.True(AntigravityTitles.Write(id, "Renamed in Nomad"));
            Assert.Equal("Renamed in Nomad", AntigravityTitles.Read(id));
            Assert.Contains("last_user_view_time:{seconds:1795552375  nanos:273000000}",
                File.ReadAllText(Path.Combine(folder, "annotations", id + ".pbtxt")));
            Assert.Equal([id + ".pbtxt"], Directory.GetFiles(Path.Combine(folder, "annotations")).Select(Path.GetFileName));

            // A chat agy never annotated gets a record of its own.
            var fresh = Guid.NewGuid().ToString();
            Assert.True(AntigravityTitles.Write(fresh, "First name"));
            Assert.Equal("First name", AntigravityTitles.Read(fresh));

            // A damaged record is neither read nor rewritten, and does not throw.
            var damaged = Guid.NewGuid().ToString();
            var damagedFile = Path.Combine(folder, "annotations", damaged + ".pbtxt");
            File.WriteAllText(damagedFile, @"title:""\UFFFFFFFF""  archived:true");
            Assert.Null(AntigravityTitles.Read(damaged));
            File.WriteAllText(damagedFile, "title:\"never closed  archived:true");
            Assert.False(AntigravityTitles.Write(damaged, "New"));
            Assert.Equal("title:\"never closed  archived:true", File.ReadAllText(damagedFile));

            // Only conversation ids name a file.
            Assert.False(AntigravityTitles.Write("..\\escape", "x"));
            Assert.Null(AntigravityTitles.Read("..\\escape"));
        }
        finally
        {
            AntigravityTitles.DataFolder = saved;
            Directory.Delete(folder, recursive: true);
        }
    }
}
