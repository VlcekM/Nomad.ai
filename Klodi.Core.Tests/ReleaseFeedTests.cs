using Klodi.Core.Updates;

namespace Klodi.Core.Tests;

public class ReleaseFeedTests
{
    [Fact]
    public void ParseKeepsPublishedVersionTagsNewestFirst()
    {
        const string json = """
            [
              { "tag_name": "v1.0.0", "name": "Klodi 1.0.0", "draft": false, "prerelease": false,
                "published_at": "2026-10-01T10:00:00Z", "body": "First", "html_url": "https://github.com/VlcekM/Klodi/releases/tag/v1.0.0" },
              { "tag_name": "v1.2.0", "name": "", "draft": true, "prerelease": false, "published_at": null, "body": "Draft" },
              { "tag_name": "v1.1.0-beta.1", "name": null, "draft": false, "prerelease": true,
                "published_at": "2026-10-20T08:30:00Z", "body": null, "html_url": "" },
              { "tag_name": "nightly", "draft": false, "prerelease": true, "published_at": "2026-10-25T00:00:00Z" }
            ]
            """;

        var releases = ReleaseFeed.Parse(json);

        Assert.Equal(["v1.1.0-beta.1", "v1.0.0"], releases.Select(r => r.Tag));
        Assert.Equal("1.1.0-beta.1", releases[0].Version);
        Assert.True(releases[0].Prerelease);
        Assert.Equal("v1.1.0-beta.1", releases[0].Title);
        Assert.Equal("", releases[0].Notes);
        Assert.Equal("Klodi 1.0.0", releases[1].Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), releases[1].Published);
    }

    [Fact]
    public void TrimNotesDropsTheDownloadSectionAndTheFullChangelogLine()
    {
        const string body = "- Faster startup\r\n- Fixed the ring\r\n\r\n## Download\r\n\r\n- `Klodi-win-x64-Setup.exe`\r\n\r\n"
            + "## What's Changed\r\n* Ring colour by @VlcekM\r\n\r\n**Full Changelog**: https://github.com/VlcekM/Klodi/compare/v1.0.0...v1.0.1\r\n";

        Assert.Equal(
            "- Faster startup\n- Fixed the ring\n\n## What's Changed\n* Ring colour by @VlcekM",
            ReleaseFeed.TrimNotes(body));
    }
}
