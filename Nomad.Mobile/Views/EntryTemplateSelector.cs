using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Views;

/// <summary>
/// Picks only the template for an <see cref="EntryViewModel"/>'s actual <see cref="EntryViewModel.Kind"/> so each row in the
/// transcript creates only its own visual tree (rather than instantiating all kinds hidden inside every row).
/// </summary>
public sealed class EntryTemplateSelector : IDataTemplate
{
    public IDataTemplate? User { get; set; }
    public IDataTemplate? Assistant { get; set; }
    public IDataTemplate? Activity { get; set; }
    public IDataTemplate? Thinking { get; set; }
    public IDataTemplate? Notice { get; set; }
    public IDataTemplate? Files { get; set; }
    public IDataTemplate? Error { get; set; }
    public IDataTemplate? Card { get; set; }

    public bool Match(object? data) => data is EntryViewModel;

    public Control? Build(object? param)
    {
        if (param is not EntryViewModel entry)
        {
            return null;
        }

        var template = entry.Kind switch
        {
            "user" => User,
            "assistant" => Assistant,
            "activity" => Activity,
            "thinking" => Thinking,
            "notice" => Notice,
            "files" => Files,
            "error" => Error,
            "permission" or "question" => Card,
            _ => Notice,
        };
        return template?.Build(param);
    }
}
