using CommunityToolkit.Mvvm.ComponentModel;

namespace Nomad.App.ViewModels;

/// <summary>A selectable row of a popup menu (model, mode, effort, folder).</summary>
public sealed partial class MenuOption : ObservableObject
{
    public MenuOption(string id, string label, string? description = null, int shortcut = 0, string? toolTip = null)
    {
        Id = id;
        Label = label;
        Description = description;
        ShortcutText = shortcut > 0 ? shortcut.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        ToolTip = toolTip;
    }

    public string Id { get; }

    public string Label { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public string ShortcutText { get; }

    public string? ToolTip { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowShortcut))]
    [NotifyPropertyChangedFor(nameof(IsDangerSelected))]
    public partial bool IsSelected { get; set; }

    /// <summary>Bypass permissions is drawn in Danger when it is the active mode.</summary>
    public bool IsDanger { get; init; }

    public bool IsDangerSelected => IsDanger && IsSelected;

    public bool ShowShortcut => !IsSelected && ShortcutText.Length > 0;
}
