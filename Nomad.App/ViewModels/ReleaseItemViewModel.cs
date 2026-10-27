using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Core.Updates;

namespace Nomad.App.ViewModels;

/// <summary>A GitHub release on the Updates page: tag, date, markers and its notes (rendered only while expanded).</summary>
public sealed partial class ReleaseItemViewModel(ReleaseInfo release, bool isInstalled, bool isNewer) : ObservableObject
{
    private readonly string _notes = ReleaseFeed.TrimNotes(release.Notes);

    public string Tag => release.Tag;

    public string DateText => release.Published == default
        ? ""
        : release.Published.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>This build's version.</summary>
    public bool IsInstalled => isInstalled;

    public bool IsNewer => isNewer;

    public bool IsPrerelease => release.Prerelease;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleNotes), nameof(ShowNoNotes))]
    public partial bool IsExpanded { get; set; }

    /// <summary>The notes while expanded, else null: a collapsed release builds no markdown controls.</summary>
    public string? VisibleNotes => IsExpanded && _notes.Length > 0 ? _notes : null;

    public bool ShowNoNotes => IsExpanded && _notes.Length == 0;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
