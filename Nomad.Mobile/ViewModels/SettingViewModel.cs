using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// One setting with its choices, as a row that opens to its list (the PC's menus, on a phone): a chat's model, effort or mode,
/// or for a new chat also its PC, agent and folder. Picking a choice closes the list and tells <see cref="Picked"/>.
/// </summary>
public sealed partial class SettingViewModel : ObservableObject
{
    public SettingViewModel(string kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    /// <summary>What it sets: <c>model</c>, <c>effort</c>, <c>mode</c>, <c>pc</c>, <c>agent</c> or <c>folder</c>.</summary>
    public string Kind { get; }

    public string Label { get; }

    public ObservableCollection<ChoiceViewModel> Choices { get; } = [];

    /// <summary>A choice was picked (not set from the PC's answer).</summary>
    public event Action<SettingViewModel, ChoiceViewModel>? Picked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    public partial ChoiceViewModel? Selected { get; private set; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>Something to choose between: hidden when there is one choice or none.</summary>
    public bool HasChoices => Choices.Count > 1;

    public string ValueText => Selected?.Name ?? "Default";

    /// <summary>Fills the choices and marks <paramref name="selected"/> (the first when it isn't one of them and <paramref name="orFirst"/>).</summary>
    public void Set(IEnumerable<ChatChoice> choices, string? selected, bool orFirst = false)
    {
        Choices.Clear();
        foreach (var choice in choices)
        {
            Choices.Add(new ChoiceViewModel(this, choice));
        }

        Select(selected, orFirst);
        OnPropertyChanged(nameof(HasChoices));
    }

    /// <summary>Marks the choice with <paramref name="id"/>, as the PC says it is now.</summary>
    public void Select(string? id, bool orFirst = false)
    {
        var match = Choices.FirstOrDefault(c => c.Id == id) ?? (orFirst ? Choices.FirstOrDefault() : null);
        foreach (var choice in Choices)
        {
            choice.IsSelected = ReferenceEquals(choice, match);
        }

        Selected = match;
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen && HasChoices;

    internal void Pick(ChoiceViewModel choice)
    {
        IsOpen = false;
        if (ReferenceEquals(choice, Selected))
        {
            return;
        }

        Select(choice.Id);
        Picked?.Invoke(this, choice);
    }
}

public sealed partial class ChoiceViewModel(SettingViewModel setting, ChatChoice choice) : ObservableObject
{
    public string Id => choice.Id;

    public string Name => choice.Name;

    public string? Description => choice.Description;

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Pick() => setting.Pick(this);
}
