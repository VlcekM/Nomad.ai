using Nomad.Core.Git;

namespace Nomad.Core.Tests;

public class GitHubWorkTests
{
    [Fact]
    public void PullRequestsGetTheirAuthorLabelsDraftAndReview()
    {
        const string json = """
            [
              {"number":4,"title":"Old","url":"https://github.com/o/r/pull/4","author":{"login":"ann"},"updatedAt":"2026-11-01T10:00:00Z",
               "labels":[],"isDraft":false,"headRefName":"fix-4","reviewDecision":""},
              {"number":7,"title":"New","url":"https://github.com/o/r/pull/7","author":{"login":"bob","is_bot":false},"updatedAt":"2026-11-18T10:00:00Z",
               "labels":[{"name":"bug","color":"d73a4a"}],"isDraft":true,"headRefName":"feat/x","reviewDecision":"APPROVED"}
            ]
            """;

        var items = GitHubWork.Parse(json, WorkKind.PullRequest);

        Assert.Equal([7, 4], items.Select(i => i.Number));
        var newest = items[0];
        Assert.Equal(("bob", true, "feat/x", "APPROVED"), (newest.Author, newest.IsDraft, newest.Branch, newest.ReviewDecision));
        Assert.Equal(["bug"], newest.Labels);
        Assert.Equal(WorkKind.PullRequest, newest.Kind);
    }

    [Fact]
    public void IssuesWithoutOptionalFieldsStillParse()
    {
        var item = Assert.Single(GitHubWork.Parse("""[{"number":2,"title":"Crash","url":"u"}]""", WorkKind.Issue));

        Assert.Equal((2, "Crash", "", false, ""), (item.Number, item.Title, item.Author, item.IsDraft, item.Branch));
        Assert.Empty(item.Labels);
    }

    [Fact]
    public void ClonesOfOneRepositoryMakeOneGroupInFolderOrder()
    {
        var a = new RepoWork("o/a", @"C:\a", [], []);
        var b = new RepoWork("o/b", @"C:\b", [], []);
        var aClone = new RepoWork("O/A", @"C:\a2", [], []);

        var groups = GitHubWork.Group([a, null, b, aClone]);

        Assert.Equal([@"C:\a", @"C:\b"], groups.Select(g => g.Folder));
    }
}
