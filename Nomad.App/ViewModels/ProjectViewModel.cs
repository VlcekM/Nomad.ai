using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.App.Infrastructure;
using Nomad.Core.Agents;
using Nomad.Core.Git;

namespace Nomad.App.ViewModels;

/// <summary>A commit line: subject, short hash and how long ago, with the author and full time as a tooltip.</summary>
public sealed record CommitRow(string Hash, string Subject, string WhenText, string ToolTip);

/// <summary>
/// A project folder with its git state: a card on the new-session page (click to work there), and the project
/// section of the side panel. The git part loads in the background; a folder that is no repository just has none.
/// </summary>
public sealed partial class ProjectViewModel : ObservableObject
{
    public ProjectViewModel(string path)
    {
        Path = path;
        Name = Format.FolderName(path);
    }

    public string Path { get; }

    public string Name { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBranch))]
    public partial string Branch { get; set; } = "";

    public bool HasBranch => Branch.Length > 0;

    /// <summary>"3 changed · ↑1 ↓2", or "Clean".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyPropertyChangedFor(nameof(HasBothFooterParts))]
    public partial string StatusText { get; set; } = "";

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>The card's footer has a git state and a last session, with a dot between them.</summary>
    public bool HasBothFooterParts => HasStatus && HasLastSession;

    [ObservableProperty]
    public partial bool HasChanges { get; set; }

    /// <summary>When Claude last worked here: "Last session 3 hours ago".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastSession))]
    [NotifyPropertyChangedFor(nameof(HasBothFooterParts))]
    public partial string LastSessionText { get; set; } = "";

    public bool HasLastSession => LastSessionText.Length > 0;

    /// <summary>The latest five commits (side panel).</summary>
    public ObservableCollection<CommitRow> Commits { get; } = [];

    /// <summary>The latest three (card).</summary>
    public ObservableCollection<CommitRow> TopCommits { get; } = [];

    [ObservableProperty]
    public partial bool HasCommits { get; set; }

    /// <summary>The git state has been read once (the card's git part fades in then).</summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    /// <summary>Loaded and found no repository.</summary>
    [ObservableProperty]
    public partial bool IsNotGit { get; set; }

    public async Task LoadAsync(bool refresh = false)
    {
        var status = await GitInfo.GetAsync(Path, 5, refresh);
        IsNotGit = status is null;
        Branch = status?.Branch ?? "";
        HasChanges = status is { Changes: > 0 };
        StatusText = status is null ? "" : Describe(status);
        Commits.Clear();
        TopCommits.Clear();
        foreach (var c in status?.Commits ?? [])
        {
            var row = new CommitRow(c.Hash, c.Subject, Format.AgoShort(c.When.UtcDateTime), $"{c.Hash} · {c.Author} · {Format.Relative(c.When.UtcDateTime)}");
            Commits.Add(row);
            if (TopCommits.Count < 3)
            {
                TopCommits.Add(row);
            }
        }

        HasCommits = Commits.Count > 0;
        IsLoaded = true;
    }

    private static string Describe(GitStatus status)
    {
        var parts = new List<string> { status.Changes == 0 ? "Clean" : $"{status.Changes} changed" };
        if (status.Ahead > 0)
        {
            parts.Add($"↑{status.Ahead}");
        }

        if (status.Behind > 0)
        {
            parts.Add($"↓{status.Behind}");
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>An MCP server in the side panel, with a dot for its state.</summary>
public sealed record ConnectorRow(string Name, string Status)
{
    public ConnectorRow(McpServerInfo server) : this(server.Name, server.Status)
    {
    }

    public bool IsConnected => Status == "connected";

    public bool IsFailed => Status == "failed";

    public bool IsWaiting => Status is "needs-auth" or "pending";

    public string StatusText => Status switch
    {
        "connected" => "Connected",
        "failed" => "Failed to connect",
        "needs-auth" => "Needs sign-in (/mcp)",
        "pending" => "Connecting",
        "configured" => "Configured; connects when the session starts",
        _ => Format.Capitalize(Status),
    };
}
