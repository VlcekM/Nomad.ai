using CommunityToolkit.Mvvm.ComponentModel;
using Klodi.App.Infrastructure;

namespace Klodi.App.ViewModels;

/// <summary>A row in the sidebar's Recents list.</summary>
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

    public bool ShowUnread => IsUnread && !IsRunning;

    public string ToolTipText => $"{WorkingDirectory ?? "No folder"}\n{Format.Relative(LastActivityUtc)}";
}
