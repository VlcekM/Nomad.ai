using CommunityToolkit.Mvvm.ComponentModel;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    public partial string? WorkingDirectory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    public partial DateTime LastActivityUtc { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnread))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnread))]
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

    /// <summary>Being archived or deleted: the row folds away before it leaves the list.</summary>
    [ObservableProperty]
    public partial bool IsLeaving { get; set; }

    public string AliveToggleText => IsAlive ? "Make idle" : "Make alive";

    public string ArchiveToggleText => IsArchived ? "Unarchive" : "Archive";

    public bool ShowUnread => IsUnread && !IsRunning;

    public string ToolTipText => $"{WorkingDirectory ?? "No folder"}\n{Format.Relative(LastActivityUtc)}" + (IsElsewhere ? "\nRunning in another Nomad window or a terminal" : "");
}

/// <summary>A group heading in the sidebar's Idle list ("Today", a folder, "Archived").</summary>
public sealed class SessionGroupHeader(string title)
{
    public string Title { get; } = title;
}
