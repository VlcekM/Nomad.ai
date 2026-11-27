using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// A permission or question card waiting on an answer. The first answer from anywhere wins: if the PC (or another phone) answered
/// first, the PC refuses this one and says so.
/// </summary>
public sealed partial class CardViewModel : ObservableObject
{
    private readonly Func<ChatInput, Task<ChatInputResult?>> _send;

    public CardViewModel(ChatCard card, Func<ChatInput, Task<ChatInputResult?>> send)
    {
        _send = send;
        Id = card.Id;
        Title = card.Title ?? (card.Questions is { Count: > 0 } ? "Claude has a question" : "Permission needed");
        Detail = card.Detail;
        Description = card.Description;
        CanAllowForSession = card.CanAllowForSession;
        AllowText = card.AllowText ?? "Allow";
        DenyText = card.DenyText ?? (card.Questions is { Count: > 0 } ? "Skip" : "Deny");
        foreach (var question in card.Questions ?? [])
        {
            var vm = new QuestionViewModel(question);
            vm.PropertyChanged += OnQuestionChanged;
            Questions.Add(vm);
        }
    }

    public string Id { get; }

    public string Title { get; }

    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description) && Description != Title;

    public bool CanAllowForSession { get; }

    public string AllowText { get; }

    public string DenyText { get; }

    public ObservableCollection<QuestionViewModel> Questions { get; } = [];

    public bool IsQuestion => Questions.Count > 0;

    public bool IsPermission => !IsQuestion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AllowCommand), nameof(AllowForSessionCommand), nameof(DenyCommand), nameof(SubmitCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Every question has an answer: an option picked, or one typed.</summary>
    public bool CanSubmit => !IsBusy && Questions.All(q => q.HasAnswer);

    private bool CanAnswer => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task AllowAsync() => AnswerAsync("allow", null);

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task AllowForSessionAsync() => AnswerAsync("session", null);

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task DenyAsync() => AnswerAsync("deny", null);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task SubmitAsync() => AnswerAsync("allow", [.. Questions.Select(q => q.ToAnswer())]);

    private async Task AnswerAsync(string choice, IReadOnlyList<ChatAnswer>? answers)
    {
        IsBusy = true;
        try
        {
            await _send(new ChatInput("answer", Card: Id, Choice: choice, Answers: answers, Id: Guid.NewGuid().ToString("N")));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnQuestionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuestionViewModel.HasAnswer))
        {
            SubmitCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSubmit));
        }
    }
}

/// <summary>One question on a question card: pick one option (or several, when it allows), or type an answer.</summary>
public sealed partial class QuestionViewModel : ObservableObject
{
    public QuestionViewModel(ChatQuestion question)
    {
        Header = question.Header;
        Question = question.Question;
        MultiSelect = question.MultiSelect;
        foreach (var option in question.Options)
        {
            Options.Add(new OptionViewModel(this, option.Label, option.Description));
        }
    }

    public string Header { get; }

    public string Question { get; }

    public bool MultiSelect { get; }

    public ObservableCollection<OptionViewModel> Options { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer))]
    public partial string Other { get; set; } = "";

    public bool HasAnswer => Options.Any(o => o.IsSelected) || Other.Trim().Length > 0;

    /// <summary>A single-choice question takes one answer: typing one clears the option picked.</summary>
    partial void OnOtherChanged(string value)
    {
        if (!MultiSelect && value.Trim().Length > 0)
        {
            foreach (var option in Options.Where(o => o.IsSelected))
            {
                option.IsSelected = false;
            }
        }
    }

    internal void OnPicked(OptionViewModel picked)
    {
        if (!MultiSelect && picked.IsSelected)
        {
            foreach (var option in Options.Where(o => !ReferenceEquals(o, picked)))
            {
                option.IsSelected = false;
            }

            Other = ""; // An option picked replaces a typed answer.
        }

        OnPropertyChanged(nameof(HasAnswer));
    }

    public ChatAnswer ToAnswer() =>
        new([.. Options.Where(o => o.IsSelected).Select(o => o.Label)], Other.Trim().Length > 0 ? Other.Trim() : null);
}

public sealed partial class OptionViewModel(QuestionViewModel question, string label, string? description) : ObservableObject
{
    public string Label { get; } = label;

    public string? Description { get; } = description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => question.OnPicked(this);
}
