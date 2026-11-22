using System.Globalization;
using System.Text.Json;

namespace Nomad.Core.Git;

/// <summary>
/// What a <see cref="WorkItem"/> is.
/// </summary>
public enum WorkKind { PullRequest, Issue }

/// <summary>
/// An issue or pull request assignee and their GitHub avatar.
/// </summary>
public sealed record WorkAssignee(string Login, string AvatarUrl)
{
    /// <summary>
    /// The letter shown while the avatar is unavailable.
    /// </summary>
    public string Initial => Login.Length == 0 ? "" : char.ToUpperInvariant(Login[0]).ToString();
}

/// <summary>
/// An open pull request or issue on GitHub, as the pull requests and issues page lists it.
/// </summary>
/// <param name="Branch">A pull request's head branch; empty for an issue.</param>
/// <param name="ReviewDecision">"APPROVED", "CHANGES_REQUESTED", "REVIEW_REQUIRED" or empty.</param>
public sealed record WorkItem(
    WorkKind Kind,
    int Number,
    string Title,
    string Url,
    string Author,
    DateTimeOffset Updated,
    IReadOnlyList<string> Labels,
    bool IsDraft = false,
    string Branch = "",
    string ReviewDecision = "",
    int CommentCount = 0,
    IReadOnlyList<WorkAssignee>? Assignees = null);

/// <summary>
/// A repository's open pull requests and issues, read in <see cref="Folder"/> (the first local checkout of it).
/// </summary>
public sealed record RepoWork(string NameWithOwner, string Folder, IReadOnlyList<WorkItem> PullRequests, IReadOnlyList<WorkItem> Issues)
{
    /// <summary>
    /// Nothing open: the page shows it as one muted line.
    /// </summary>
    public bool IsEmpty => PullRequests.Count == 0 && Issues.Count == 0;
}

/// <summary>
/// The open pull requests and issues of a repository, read with <c>gh</c> (the user's own
/// GitHub sign-in) and cached for a minute. Best effort: no gh, not signed in or not a GitHub repository gives null.
/// </summary>
public static class GitHubWork
{
    #region Reading

    /// <summary>
    /// A result this young is given again rather than asked for anew.
    /// </summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A failure (no gh, not a GitHub repository) is remembered longer, so it isn't asked again and again.
    /// </summary>
    private static readonly TimeSpan FreshFailure = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long one gh call may take.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The newest 50 open items of each kind, with comment totals and small assignee avatars.
    /// </summary>
    private const string WorkQuery = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            nameWithOwner
            pullRequests(first: 50, states: OPEN, orderBy: {field: CREATED_AT, direction: DESC}) {
              nodes { ...PullRequestRow }
            }
            issues(first: 50, states: OPEN, orderBy: {field: CREATED_AT, direction: DESC}) {
              nodes { ...IssueRow }
            }
          }
        }
        fragment PullRequestRow on PullRequest {
          number title url author { login } updatedAt labels(first: 100) { nodes { name } }
          isDraft headRefName reviewDecision
          comments { totalCount }
          assignees(first: 10) { nodes { login avatarUrl(size: 48) } }
        }
        fragment IssueRow on Issue {
          number title url author { login } updatedAt labels(first: 100) { nodes { name } }
          comments { totalCount }
          assignees(first: 10) { nodes { login avatarUrl(size: 48) } }
        }
        """;

    /// <summary>
    /// The latest read per repository folder.
    /// </summary>
    private static readonly Dictionary<string, (DateTime At, Task<RepoWork?> Task)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The open work of the repository a folder is in (a worktree counts as its repository); <paramref name="refresh"/> asks
    /// GitHub again even when the last answer is fresh.
    /// </summary>
    public static Task<RepoWork?> GetAsync(string folder, bool refresh = false)
    {
        var key = CacheKey(folder);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit) && (!hit.Task.IsCompleted || (!refresh && IsFresh(hit.At, hit.Task))))
            {
                return hit.Task;
            }

            var task = Task.Run(() => ReadAsync(key));
            Cache[key] = (DateTime.UtcNow, task);
            return task;
        }
    }

    /// <summary>
    /// Shares a cached result with worktrees of the same checkout.
    /// </summary>
    private static string CacheKey(string folder) => (GitInfo.WorktreeOwner(folder) ?? folder).TrimEnd('\\', '/');

    /// <summary>
    /// A finished read young enough to give again (a failure stays longer).
    /// </summary>
    private static bool IsFresh(DateTime at, Task<RepoWork?> task)
    {
        var failed = task.IsFaulted || task is { IsCompletedSuccessfully: true, Result: null };
        return DateTime.UtcNow - at < (failed ? FreshFailure : Fresh);
    }

    /// <summary>
    /// Resolves the checkout with gh and reads the open work from the same GitHub host.
    /// </summary>
    private static async Task<RepoWork?> ReadAsync(string folder)
    {
        if (!Directory.Exists(folder)) return null;

        var repo = await Gh(folder, "repo", "view", "--json", "nameWithOwner,url");
        if (repo is null) return null;

        try
        {
            using var identity = JsonDocument.Parse(repo);
            var name = Str(identity.RootElement, "nameWithOwner");
            if (name.Split('/') is not [var owner, var repository]) return null;
            if (!Uri.TryCreate(Str(identity.RootElement, "url"), UriKind.Absolute, out var url)) return null;

            var json = await Gh(folder, "api", "graphql", "--hostname", url.Host,
                "-f", $"query={WorkQuery}", "-f", $"owner={owner}", "-f", $"name={repository}");
            if (json is null) return null;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            if (!data.TryGetProperty("repository", out var work) || work.ValueKind != JsonValueKind.Object) return null;

            return new RepoWork(Str(work, "nameWithOwner"), folder,
                ParseItems(Nodes(work, "pullRequests"), WorkKind.PullRequest), ParseItems(Nodes(work, "issues"), WorkKind.Issue));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds the signed-in GitHub user to the issue's assignees and returns any error to display.
    /// </summary>
    public static async Task<string?> AssignToSelfAsync(string folder, string issueUrl)
    {
        var (ok, _, error) = await GitInfo.RunToolAsync("gh", ["issue", "edit", issueUrl, "--add-assignee", "@me"], folder, Timeout);
        if (!ok) return string.IsNullOrWhiteSpace(error) ? "GitHub CLI could not update the assignees." : error.Trim();

        var key = CacheKey(folder);
        lock (Cache)
        {
            Cache.Remove(key);
        }

        return null;
    }

    /// <summary>
    /// Runs gh in the folder: its output, or null when it failed or took too long.
    /// </summary>
    private static async Task<string?> Gh(string folder, params string[] args)
    {
        var (ok, output, _) = await GitInfo.RunToolAsync("gh", args, folder, Timeout);
        return ok ? output : null;
    }

    #endregion

    #region Parsing and grouping

    /// <summary>
    /// The items of a <c>gh pr list</c> or <c>gh issue list</c> JSON array, the most recently updated first.
    /// </summary>
    public static IReadOnlyList<WorkItem> Parse(string json, WorkKind kind)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseItems(doc.RootElement.EnumerateArray(), kind);
    }

    /// <summary>
    /// Parses rows in most recently updated order.
    /// </summary>
    private static IReadOnlyList<WorkItem> ParseItems(IEnumerable<JsonElement> items, WorkKind kind) =>
        [.. items.Select(e => ParseItem(e, kind)).OrderByDescending(i => i.Updated)];

    /// <summary>
    /// One element of gh's list.
    /// </summary>
    private static WorkItem ParseItem(JsonElement e, WorkKind kind)
    {
        var author = e.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "login") : "";
        var updated = DateTimeOffset.TryParse(Str(e, "updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : DateTimeOffset.MinValue;
        var draft = e.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;

        var comments = e.TryGetProperty("comments", out var c) && c.ValueKind == JsonValueKind.Object ? Int(c, "totalCount") : 0;
        var labels = Nodes(e, "labels").Select(l => Str(l, "name")).Where(n => n.Length > 0).ToArray();
        var assignees = Nodes(e, "assignees").Select(a => new WorkAssignee(Str(a, "login"), Str(a, "avatarUrl")))
            .Where(a => a.Login.Length > 0).ToArray();

        return new WorkItem(kind, Int(e, "number"), Str(e, "title"), Str(e, "url"), author, updated, labels, draft,
            Str(e, "headRefName"), Str(e, "reviewDecision"), comments, assignees);
    }

    /// <summary>
    /// The entries of a GraphQL connection or a flat gh CLI array.
    /// </summary>
    private static IEnumerable<JsonElement> Nodes(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var nodes)) return [];
        if (nodes.ValueKind == JsonValueKind.Object && !nodes.TryGetProperty("nodes", out nodes)) return [];

        return nodes.ValueKind == JsonValueKind.Array ? nodes.EnumerateArray() : [];
    }

    /// <summary>
    /// One group per repository, in the order its folders came (two clones or worktrees of one repository make one group,
    /// the first folder's).
    /// </summary>
    public static IReadOnlyList<RepoWork> Group(IEnumerable<RepoWork?> repos) =>
    [
        .. repos
            .OfType<RepoWork>()
            .DistinctBy(r => r.NameWithOwner, StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// A string property, or empty.
    /// </summary>
    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// A number property, or 0.
    /// </summary>
    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    #endregion
}
