using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Nomad.App.Controls;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Views;

/// <summary>A chat's state as the desktop sidebar's mark: a spinning circle while it works, a yellow dot while it waits on you.</summary>
public sealed class StateMarkConverter : IValueConverter
{
    public static readonly StateMarkConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "working" => SessionMark.Working,
        "waiting" => SessionMark.Waiting,
        _ => SessionMark.None,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// An activity step's icon, as the desktop's tool rows pick it (Nomad.App's ToolText.IconKey, by the tool's name): a terminal
/// for commands, a pencil for edits, a page for reads, a magnifier for searches; the thought icon for thinking.
/// </summary>
public sealed class StepIconConverter : IValueConverter
{
    public static readonly StepIconConverter Instance = new();

    public static string IconKey(StepViewModel step) => step.IsThought ? "IconThought" : step.Title switch
    {
        "Bash" or "PowerShell" or "BashOutput" or "KillShell" => "IconTerminal",
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "IconPencil",
        "Read" or "NotebookRead" => "IconFile",
        "Grep" or "Glob" or "LS" => "IconSearch",
        "WebFetch" or "WebSearch" => "IconGlobe",
        "Task" or "Agent" => "IconAgent",
        "TodoWrite" => "IconList",
        "AskUserQuestion" or "mcp__nomad_session__request_folder_move" => "IconQuestion",
        "mcp__nomad_session__suggest_new_chat" => "IconFork",
        _ => "IconTool",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is StepViewModel step && Application.Current?.TryGetResource(IconKey(step), null, out var resource) == true && resource is Geometry geometry
            ? geometry
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Small text tests for the views.</summary>
public static class StringIs
{
    /// <summary>A chat that has stopped (its row's title is muted, as the desktop's idle sessions).</summary>
    public static readonly IValueConverter Stopped = new FuncValueConverter<string?, bool>(state => state == "stopped");

    /// <summary>" · " after a part that is there, nothing after one that isn't.</summary>
    public static readonly IValueConverter Separator = new FuncValueConverter<string?, string>(part => string.IsNullOrEmpty(part) ? "" : " · ");
}
