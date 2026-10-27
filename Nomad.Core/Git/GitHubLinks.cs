using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nomad.Core.Git;

public enum PullRequestState { Open, Draft, Merged, Closed }

public enum IssueState { Open, Completed, NotPlanned }

public sealed record PullRequestLink(int Number, string Title, PullRequestState State, string Url, string Branch, IReadOnlyList<int> Closes);

public sealed record IssueLink(int Number, string Title, IssueState State, string Url);

/// <summary>A repository's recent pull requests and issues on GitHub, as the <c>gh</c> CLI lists them.</summary>
public sealed class RepoLinks(string nameWithOwner, IReadOnlyList<PullRequestLink> pullRequests, IReadOnlyList<IssueLink> issues)
{
    /// <summary>"VlcekM/Nomad.ai".</summary>
    public string NameWithOwner { get; } = nameWithOwner;

    public string Owner => NameWithOwner.Split('/')[0];

    /// <summary>
    /// A pull request or issue URL a session mentions counts only under this repository's owner: transcripts also
    /// quote unrelated links (documentation, tool output).
    /// </summary>
    public bool Trusts(string url) =>
        url.StartsWith($"https://github.com/{Owner}/", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PullRequestLink> PullRequests { get; } = pullRequests;

    public IReadOnlyList<IssueLink> Issues { get; } = issues;

    /// <summary>The newest pull request from this branch.</summary>
    public PullRequestLink? PullRequestFor(string? branch) =>
        string.IsNullOrEmpty(branch) || branch == "HEAD" ? null : PullRequests.FirstOrDefault(p => p.Branch == branch);

    /// <summary>
    /// The issue a session works on: the one its pull request closes, else a number in its branch ("issue-95",
    /// "95-fix-x", "feat/95-x"), else one in its title ("#95", "issue 95"). Only issues the repository has.
    /// </summary>
    public IssueLink? IssueFor(string? branch, string? title, PullRequestLink? pr)
    {
        foreach (var number in Candidates(branch, title, pr))
        {
            if (Issues.FirstOrDefault(i => i.Number == number) is { } issue)
            {
                return issue;
            }
        }

        return null;
    }

    private static IEnumerable<int> Candidates(string? branch, string? title, PullRequestLink? pr)
    {
        foreach (var number in pr?.Closes ?? [])
        {
            yield return number;
        }

        if (!string.IsNullOrEmpty(branch))
        {
            foreach (var pattern in (Regex[])[BranchIssue, BranchLead])
            {
                if (Number(pattern.Match(branch)) is { } number)
                {
                    yield return number;
                }
            }
        }

        if (!string.IsNullOrEmpty(title) && Number(TitleIssue.Match(title)) is { } inTitle)
        {
            yield return inTitle;
        }
    }

    private static int? Number(Match match) =>
        match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    private static readonly Regex BranchIssue = new(@"(?i)(?:issues?|gh)[-_/#]?(\d{1,6})(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex BranchLead = new(@"^(?:[A-Za-z]+/)?(\d{1,6})[-_]", RegexOptions.CultureInvariant);
    private static readonly Regex TitleIssue = new(@"(?i)(?:#|\bissue\s+#?)(\d{1,6})\b", RegexOptions.CultureInvariant);
}

/// <summary>
/// Pull requests and issues per repository, read with <c>gh pr list</c> and <c>gh issue list</c> (the user's own
/// GitHub sign-in) and cached for a few minutes. Best effort: no gh, not signed in or not a GitHub repository gives null,
/// which is remembered for longer so it isn't asked again and again.
/// </summary>
public static class GitHubLinks
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FreshFailure = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly Dictionary<string, (DateTime At, Task<RepoLinks?> Task)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The links of the repository a folder is in (a worktree counts as its repository).</summary>
    public static Task<RepoLinks?> GetAsync(string folder)
    {
        var key = (GitInfo.WorktreeOwner(folder) ?? folder).TrimEnd('\\', '/');
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                var age = DateTime.UtcNow - hit.At;
                var failed = hit.Task is { IsCompletedSuccessfully: true, Result: null } || hit.Task.IsFaulted;
                if (!hit.Task.IsCompleted || age < (failed ? FreshFailure : Fresh))
                {
                    return hit.Task;
                }
            }

            var task = Task.Run(() => ReadAsync(key));
            Cache[key] = (DateTime.UtcNow, task);
            return task;
        }
    }

    private static async Task<RepoLinks?> ReadAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var repo = Gh(folder, "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner");
        var prs = Gh(folder, "pr", "list", "--state", "all", "--limit", "100", "--json",
            "number,title,state,isDraft,url,headRefName,closingIssuesReferences");
        var issues = Gh(folder, "issue", "list", "--state", "all", "--limit", "200", "--json", "number,title,state,stateReason,url");
        var prText = await prs;

        // Older gh without closingIssuesReferences: the same list without it.
        prText ??= await Gh(folder, "pr", "list", "--state", "all", "--limit", "100", "--json", "number,title,state,isDraft,url,headRefName");
        var issueText = await issues;
        if ((await repo)?.Trim() is not { Length: > 0 } name || (prText is null && issueText is null))
        {
            return null;
        }

        try
        {
            return new RepoLinks(name, ParsePullRequests(prText ?? "[]"), ParseIssues(issueText ?? "[]"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly Dictionary<string, (DateTime At, Task<object?> Task)> ByUrl = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One pull request by its URL (any repository the user can see), cached like the lists.</summary>
    public static async Task<PullRequestLink?> PullRequestAsync(string url, string folder) =>
        await ByUrlAsync(url, async () =>
        {
            var json = await Gh(folder, "pr", "view", url, "--json", "number,title,state,isDraft,url,headRefName,closingIssuesReferences")
                ?? await Gh(folder, "pr", "view", url, "--json", "number,title,state,isDraft,url,headRefName");
            return json is null ? null : ParsePullRequests($"[{json}]").FirstOrDefault();
        }) as PullRequestLink;

    /// <summary>One issue by its URL.</summary>
    public static async Task<IssueLink?> IssueAsync(string url, string folder) =>
        await ByUrlAsync(url, async () =>
        {
            var json = await Gh(folder, "issue", "view", url, "--json", "number,title,state,stateReason,url");
            return json is null ? null : ParseIssues($"[{json}]").FirstOrDefault();
        }) as IssueLink;

    private static Task<object?> ByUrlAsync(string url, Func<Task<object?>> read)
    {
        lock (ByUrl)
        {
            if (ByUrl.TryGetValue(url, out var hit) && (!hit.Task.IsCompleted || DateTime.UtcNow - hit.At < Fresh))
            {
                return hit.Task;
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    return await read();
                }
                catch (JsonException)
                {
                    return null;
                }
            });
            ByUrl[url] = (DateTime.UtcNow, task);
            return task;
        }
    }

    private static async Task<string?> Gh(string folder, params string[] args)
    {
        var (ok, output, _) = await GitInfo.RunToolAsync("gh", args, folder, Timeout);
        return ok ? output : null;
    }

    public static IReadOnlyList<PullRequestLink> ParsePullRequests(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<PullRequestLink>();
        foreach (var pr in doc.RootElement.EnumerateArray())
        {
            var state = Str(pr, "state") switch
            {
                "MERGED" => PullRequestState.Merged,
                "CLOSED" => PullRequestState.Closed,
                _ when pr.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True => PullRequestState.Draft,
                _ => PullRequestState.Open,
            };
            var closes = new List<int>();
            if (pr.TryGetProperty("closingIssuesReferences", out var refs) && refs.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in refs.EnumerateArray())
                {
                    if (issue.TryGetProperty("number", out var n) && n.TryGetInt32(out var number))
                    {
                        closes.Add(number);
                    }
                }
            }

            list.Add(new PullRequestLink(Int(pr, "number"), Str(pr, "title"), state, Str(pr, "url"), Str(pr, "headRefName"), closes));
        }

        return list;
    }

    public static IReadOnlyList<IssueLink> ParseIssues(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<IssueLink>();
        foreach (var issue in doc.RootElement.EnumerateArray())
        {
            var state = (Str(issue, "state"), Str(issue, "stateReason")) switch
            {
                ("CLOSED", "NOT_PLANNED" or "DUPLICATE") => IssueState.NotPlanned,
                ("CLOSED", _) => IssueState.Completed,
                _ => IssueState.Open,
            };
            list.Add(new IssueLink(Int(issue, "number"), Str(issue, "title"), state, Str(issue, "url")));
        }

        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
}
