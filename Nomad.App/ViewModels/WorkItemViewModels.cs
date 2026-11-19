using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.App.Infrastructure;
using Nomad.Core.Git;

namespace Nomad.App.ViewModels;

/// <summary>
/// One open pull request or issue on the pull requests and issues page, with the session working on it, if any.
/// </summary>
public sealed partial class WorkRowViewModel(WorkItem item) : ObservableObject
{
    #region The item

    /// <summary>
    /// What GitHub last said about it (a refresh replaces it).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(MetaText), nameof(Labels), nameof(HasLabels), nameof(IsDraft), nameof(IsReadyPullRequest))]
    [NotifyPropertyChangedFor(nameof(ReviewText), nameof(HasReview), nameof(IsApproved))]
    public partial WorkItem Item { get; set; } = item;

    /// <summary>
    /// Its page on GitHub; also what identifies the row across refreshes.
    /// </summary>
    public string Url => Item.Url;

    /// <summary>
    /// A pull request: Review and Merge buttons.
    /// </summary>
    public bool IsPullRequest => Item.Kind == WorkKind.PullRequest;

    /// <summary>
    /// An issue: a Fix button.
    /// </summary>
    public bool IsIssue => Item.Kind == WorkKind.Issue;

    /// <summary>
    /// "#42".
    /// </summary>
    public string NumberText => $"#{Item.Number}";

    /// <summary>
    /// Its title on GitHub.
    /// </summary>
    public string Title => Item.Title;

    /// <summary>
    /// Its labels' names, shown as small chips.
    /// </summary>
    public IReadOnlyList<string> Labels => Item.Labels;

    /// <summary>
    /// It has labels to show.
    /// </summary>
    public bool HasLabels => Item.Labels.Count > 0;

    /// <summary>
    /// A draft pull request: its icon is grey.
    /// </summary>
    public bool IsDraft => Item.IsDraft;

    /// <summary>
    /// An open pull request that is no draft: its icon is green (a draft's grey).
    /// </summary>
    public bool IsReadyPullRequest => IsPullRequest && !Item.IsDraft;

    /// <summary>
    /// "bob · updated 3 hours ago", or for a draft "Draft · bob · updated 3 hours ago".
    /// </summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (Item.IsDraft) parts.Add("Draft");
            if (Item.Author.Length > 0) parts.Add(Item.Author);
            if (Item.Updated > DateTimeOffset.MinValue) parts.Add("updated " + Format.Since(Item.Updated.UtcDateTime));

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// A pull request's review state: "Approved" or "Changes requested"; empty when none was given.
    /// </summary>
    public string ReviewText => Item.ReviewDecision switch
    {
        "APPROVED" => "Approved",
        "CHANGES_REQUESTED" => "Changes requested",
        _ => "",
    };

    /// <summary>
    /// A review decision to show.
    /// </summary>
    public bool HasReview => ReviewText.Length > 0;

    /// <summary>
    /// Approved: the review text is green rather than yellow.
    /// </summary>
    public bool IsApproved => Item.ReviewDecision == "APPROVED";

    /// <summary>
    /// Merged or closed since: the row folds away before it leaves the list.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial bool IsLeaving { get; set; }

    /// <summary>
    /// Still open: the row is unfolded.
    /// </summary>
    public bool IsShown => !IsLeaving;

    #endregion

    #region Its session

    /// <summary>
    /// The latest session that works on it (started here, or one whose transcript names it): a chip that opens it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession), nameof(SessionToolTip))]
    public partial SessionListItemViewModel? Session { get; set; }

    /// <summary>
    /// Started from this page and not listed yet (its CLI hasn't given it an id): the chip shows it starting.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession), nameof(IsStarting), nameof(SessionToolTip))]
    public partial SessionViewModel? Starting { get; set; }

    /// <summary>
    /// A button was clicked and its session is being made (a worktree first, for a review or a merge).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession), nameof(IsStarting), nameof(SessionToolTip))]
    public partial bool IsLaunching { get; set; }

    /// <summary>
    /// A session works on it, or one is starting: the chip shows.
    /// </summary>
    public bool HasSession => Session is not null || Starting is not null || IsLaunching;

    /// <summary>
    /// Its session is being made or hasn't got its id: the chip spins.
    /// </summary>
    public bool IsStarting => Session is null && (Starting is not null || IsLaunching);

    /// <summary>
    /// The chip's tooltip: which session it opens.
    /// </summary>
    public string SessionToolTip => Session is { } row ? $"Open \"{row.Title}\"" : "Starting a session";

    /// <summary>
    /// Why its session couldn't start (a worktree git couldn't make, say); empty otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = "";

    /// <summary>
    /// An error to show under the row.
    /// </summary>
    public bool HasError => Error.Length > 0;

    #endregion
}

/// <summary>
/// A repository on the pull requests and issues page: its open pull requests, then its open issues.
/// </summary>
public sealed partial class WorkGroupViewModel(string nameWithOwner, string folder) : ObservableObject
{
    /// <summary>
    /// How long a closed item's row takes to fold away before it is taken out.
    /// </summary>
    private static readonly TimeSpan FoldTime = TimeSpan.FromMilliseconds(220);

    /// <summary>
    /// "VlcekM/Nomad.ai".
    /// </summary>
    public string NameWithOwner { get; } = nameWithOwner;

    /// <summary>
    /// The checkout its sessions start in.
    /// </summary>
    [ObservableProperty]
    public partial string Folder { get; set; } = folder;

    /// <summary>
    /// "Nomad.ai", the repository part.
    /// </summary>
    public string Name => NameWithOwner.Split('/') is [_, var name] ? name : NameWithOwner;

    /// <summary>
    /// The owner part, shown muted after the name.
    /// </summary>
    public string Owner => NameWithOwner.Split('/')[0];

    /// <summary>
    /// The repository on GitHub (its name opens it).
    /// </summary>
    public string Url => $"https://github.com/{NameWithOwner}";

    /// <summary>
    /// Its open pull requests, the most recently updated first.
    /// </summary>
    public ObservableCollection<WorkRowViewModel> PullRequests { get; } = [];

    /// <summary>
    /// Its open issues, the most recently updated first.
    /// </summary>
    public ObservableCollection<WorkRowViewModel> Issues { get; } = [];

    /// <summary>
    /// Counts updates, so the properties that read the lists' counts are raised again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPullRequests), nameof(HasIssues), nameof(IsEmpty))]
    public partial int Revision { get; set; }

    /// <summary>
    /// It has open pull requests.
    /// </summary>
    public bool HasPullRequests => PullRequests.Count > 0;

    /// <summary>
    /// It has open issues.
    /// </summary>
    public bool HasIssues => Issues.Count > 0;

    /// <summary>
    /// Nothing open.
    /// </summary>
    public bool IsEmpty => !HasPullRequests && !HasIssues;

    /// <summary>
    /// "2 pull requests · 3 issues", leaving out a kind it has none of.
    /// </summary>
    public string CountText
    {
        get
        {
            var parts = new List<string>();
            if (HasPullRequests) parts.Add(Count(PullRequests.Count, "pull request"));
            if (HasIssues) parts.Add(Count(Issues.Count, "issue"));

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Takes GitHub's latest lists: rows stay for items still open (their text updated), new ones come in, and closed ones
    /// fold away first.
    /// </summary>
    public void Update(RepoWork work)
    {
        Folder = work.Folder;
        var gone = Gone(PullRequests, work.PullRequests).Concat(Gone(Issues, work.Issues)).ToList();
        if (gone.Count == 0)
        {
            Apply(work);
            return;
        }

        foreach (var row in gone)
        {
            row.IsLeaving = true;
        }

        DispatcherTimer.RunOnce(() => Apply(work), FoldTime);
    }

    private void Apply(RepoWork work)
    {
        Sync(PullRequests, work.PullRequests);
        Sync(Issues, work.Issues);
        Revision++;
        OnPropertyChanged(nameof(CountText));
    }

    private static IEnumerable<WorkRowViewModel> Gone(IEnumerable<WorkRowViewModel> rows, IReadOnlyList<WorkItem> items) =>
        rows.Where(r => !r.IsLeaving && !items.Any(i => string.Equals(i.Url, r.Url, StringComparison.OrdinalIgnoreCase)));

    private static void Sync(ObservableCollection<WorkRowViewModel> rows, IReadOnlyList<WorkItem> items)
    {
        var byUrl = rows.ToDictionary(r => r.Url, StringComparer.OrdinalIgnoreCase);
        var wanted = items.Select(i => Row(byUrl, i)).ToList();
        MainViewModel.SyncList(rows, wanted);
    }

    private static WorkRowViewModel Row(Dictionary<string, WorkRowViewModel> byUrl, WorkItem item)
    {
        if (!byUrl.TryGetValue(item.Url, out var row)) return new WorkRowViewModel(item);

        row.Item = item;
        row.IsLeaving = false;
        return row;
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
