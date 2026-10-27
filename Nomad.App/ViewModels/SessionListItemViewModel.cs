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
    public partial bool IsPhone { get; set; }

    public string AliveToggleText => IsAlive ? "Make idle" : "Make alive";

    public bool ShowUnread => IsUnread && !IsRunning;

    public string ToolTipText => $"{WorkingDirectory ?? "No folder"}\n{Format.Relative(LastActivityUtc)}";
}
