using System.Globalization;
using System.Text.Json;

namespace Nomad.Core.Git;

/// <summary>
/// What a <see cref="WorkItem"/> is.
/// </summary>
public enum WorkKind { PullRequest, Issue }

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
    string ReviewDecision = "");

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
/// The open pull requests and issues of a repository, read with <c>gh pr list</c> and <c>gh issue list</c> (the user's own
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
    /// How many open pull requests and issues are listed per repository.
    /// </summary>
    private const string Limit = "50";

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
        var key = (GitInfo.WorktreeOwner(folder) ?? folder).TrimEnd('\\', '/');
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
    /// A finished read young enough to give again (a failure stays longer).
    /// </summary>
    private static bool IsFresh(DateTime at, Task<RepoWork?> task)
    {
        var failed = task.IsFaulted || task is { IsCompletedSuccessfully: true, Result: null };
        return DateTime.UtcNow - at < (failed ? FreshFailure : Fresh);
    }

    /// <summary>
    /// Asks gh for the repository's name and its open pull requests and issues, all at once.
    /// </summary>
    private static async Task<RepoWork?> ReadAsync(string folder)
    {
        if (!Directory.Exists(folder)) return null;

        var repo = Gh(folder, "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner");
        var prs = Gh(folder, "pr", "list", "--state", "open", "--limit", Limit, "--json",
            "number,title,url,author,updatedAt,labels,isDraft,headRefName,reviewDecision");
        var issues = Gh(folder, "issue", "list", "--state", "open", "--limit", Limit, "--json", "number,title,url,author,updatedAt,labels");
        var name = (await repo)?.Trim();
        var prText = await prs;
        var issueText = await issues;
        if (string.IsNullOrEmpty(name) || (prText is null && issueText is null)) return null;

        try
        {
            return new RepoWork(name, folder, Parse(prText ?? "[]", WorkKind.PullRequest), Parse(issueText ?? "[]", WorkKind.Issue));
        }
        catch (JsonException)
        {
            return null;
        }
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
        var list = new List<WorkItem>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            list.Add(ParseItem(e, kind));
        }

        return [.. list.OrderByDescending(i => i.Updated)];
    }

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

        return new WorkItem(kind, Int(e, "number"), Str(e, "title"), Str(e, "url"), author, updated, Labels(e), draft,
            Str(e, "headRefName"), Str(e, "reviewDecision"));
    }

    /// <summary>
    /// The names of an item's labels.
    /// </summary>
    private static IReadOnlyList<string> Labels(JsonElement e)
    {
        if (!e.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array) return [];

        return [.. labels.EnumerateArray().Select(l => Str(l, "name")).Where(n => n.Length > 0)];
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
