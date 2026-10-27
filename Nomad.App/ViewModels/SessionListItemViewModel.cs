using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.App.Controls;
using Nomad.App.Infrastructure;

namespace Nomad.App.ViewModels;

/// <summary>A row in the sidebar's Alive or Idle list.</summary>
public sealed partial class SessionListItemViewModel : ObservableObject
{
    public SessionListItemViewModel(string sessionId)
    {
        SessionId = sessionId;
    }

    public string SessionId { get; }

    public string? FilePath { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    /// <summary>The row shows a text box with <see cref="RenameText"/> in place of the title.</summary>
    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    public partial string? WorkingDirectory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    public partial DateTime LastActivityUtc { get; set; }

    /// <summary>The row shows <see cref="ProjectName"/> on a second line (the Alive list's "Show project").</summary>
    [ObservableProperty]
    public partial bool ShowProject { get; set; }

    /// <summary>Its folder's project ("No folder" for a scratch one): set while <see cref="ShowProject"/> is on.</summary>
    [ObservableProperty]
    public partial string ProjectName { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    public partial bool IsUnread { get; set; }

    /// <summary>The session has a running CLI process (the sidebar's Alive list).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AliveToggleText))]
    public partial bool IsAlive { get; set; }

    /// <summary>Started from the phone and still running there (through a Remote Control server).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManage))]
    public partial bool IsPhone { get; set; }

    /// <summary>A Claude Code process outside this Nomad runs it: another Nomad window, or a terminal.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManage))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    public partial bool IsElsewhere { get; set; }

    /// <summary>Its process is this Nomad's to start, end, archive or delete.</summary>
    public bool CanManage => !IsPhone && !IsElsewhere;

    /// <summary>Archived from the sidebar: listed only with "Show archived" on, or when a search finds it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveToggleText))]
    public partial bool IsArchived { get; set; }

    /// <summary>Pinned: listed first in its list, its process never ended for being idle, never auto-archived.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinToggleText))]
    public partial bool IsPinned { get; set; }

    public string PinToggleText => IsPinned ? "Unpin" : "Pin";

    /// <summary>Being archived or deleted: the row folds away before it leaves the list.</summary>
    [ObservableProperty]
    public partial bool IsLeaving { get; set; }

    /// <summary>Ctrl+1…9 opens it: its place among the rows the sidebar shows (0: none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowShortcut))]
    public partial int ShortcutNumber { get; set; }

    /// <summary>Ctrl is held: the row shows its number.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowShortcut))]
    public partial bool ShortcutHint { get; set; }

    public bool ShowShortcut => ShortcutHint && ShortcutNumber > 0;

    public string AliveToggleText => IsAlive ? "Make idle" : "Make alive";

    public string ArchiveToggleText => IsArchived ? "Unarchive" : "Archive";

    /// <summary>Claude waits for an answer: a permission, or a question.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    public partial bool IsWaiting { get; set; }

    /// <summary>The mark on the right: asking beats working, working beats unread.</summary>
    public SessionMark Mark => IsWaiting ? SessionMark.Waiting : IsRunning ? SessionMark.Working : IsUnread ? SessionMark.Unread : SessionMark.None;

    /// <summary>The branch its transcript last recorded (null: not a repository, or not known yet).</summary>
    public string? GitBranch { get; set; }

    /// <summary>The last GitHub pull request and issue URLs its transcript mentions.</summary>
    public string? PullRequestUrl { get; set; }

    public string? IssueUrl { get; set; }

    /// <summary>The GitHub pull request from its branch, if any: an icon on the row (click opens it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPullRequest))]
    [NotifyPropertyChangedFor(nameof(PullRequestToolTip))]
    [NotifyPropertyChangedFor(nameof(IsPrOpen))]
    [NotifyPropertyChangedFor(nameof(IsPrDraft))]
    [NotifyPropertyChangedFor(nameof(IsPrMerged))]
    [NotifyPropertyChangedFor(nameof(IsPrClosed))]
    public partial Nomad.Core.Git.PullRequestLink? PullRequest { get; set; }

    /// <summary>The GitHub issue it works on (closed by its pull request, or named in its branch or title).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssue))]
    [NotifyPropertyChangedFor(nameof(IssueToolTip))]
    [NotifyPropertyChangedFor(nameof(IsIssueOpen))]
    [NotifyPropertyChangedFor(nameof(IsIssueDone))]
    [NotifyPropertyChangedFor(nameof(IsIssueNotPlanned))]
    public partial Nomad.Core.Git.IssueLink? Issue { get; set; }

    /// <summary>It ran in a worktree whose branch has been merged into its repository: a merged icon on the row's left.</summary>
    [ObservableProperty]
    public partial bool IsWorktreeMerged { get; set; }

    public bool HasPullRequest => PullRequest is not null;

    public bool HasIssue => Issue is not null;

    // The state picks the icon and its colour (GitHub's: green open, grey draft, purple merged or done, red closed).
    public bool IsPrOpen => PullRequest?.State == Nomad.Core.Git.PullRequestState.Open;

    public bool IsPrDraft => PullRequest?.State == Nomad.Core.Git.PullRequestState.Draft;

    public bool IsPrMerged => PullRequest?.State == Nomad.Core.Git.PullRequestState.Merged;

    public bool IsPrClosed => PullRequest?.State == Nomad.Core.Git.PullRequestState.Closed;

    public bool IsIssueOpen => Issue?.State == Nomad.Core.Git.IssueState.Open;

    public bool IsIssueDone => Issue?.State == Nomad.Core.Git.IssueState.Completed;

    public bool IsIssueNotPlanned => Issue?.State == Nomad.Core.Git.IssueState.NotPlanned;

    public string PullRequestToolTip => PullRequest is { } pr
        ? $"Pull request #{pr.Number} · {pr.State switch { Nomad.Core.Git.PullRequestState.Draft => "Draft", Nomad.Core.Git.PullRequestState.Merged => "Merged", Nomad.Core.Git.PullRequestState.Closed => "Closed", _ => "Open" }}\n{pr.Title}"
        : "";

    public string IssueToolTip => Issue is { } issue
        ? $"Issue #{issue.Number} · {issue.State switch { Nomad.Core.Git.IssueState.Completed => "Closed", Nomad.Core.Git.IssueState.NotPlanned => "Closed as not planned", _ => "Open" }}\n{issue.Title}"
        : "";

    public string ToolTipText =>$"{WorkingDirectory ?? "No folder"}\n{Format.Relative(LastActivityUtc)}" + (IsElsewhere ? "\nRunning in another Nomad window or a terminal" : "");
}

/// <summary>A group heading in the sidebar's Alive or Idle list ("Today", a folder, "Archived"): a click folds its rows away.</summary>
public sealed partial class SessionGroupHeader(string key, string title) : ObservableObject
{
    /// <summary>"Date:Today", "Folder:Nomad.ai", "Archived": how its collapsed state is remembered.</summary>
    public string Key { get; } = key;

    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsCollapsed { get; set; }

    /// <summary>How many sessions it holds: shown while it is collapsed.</summary>
    [ObservableProperty]
    public partial int Count { get; set; }
}
