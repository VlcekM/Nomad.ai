using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.App.Infrastructure;

namespace Nomad.App.ViewModels;

/// <summary>
/// A command started with <c>run_in_background</c> (Bash, PowerShell): it runs past its call and the turn, until a task
/// notification says it ended or the CLI process goes.
/// </summary>
public sealed partial class BackgroundTaskViewModel : ObservableObject
{
    public BackgroundTaskViewModel(string toolUseId, string toolName, JsonElement input)
    {
        ToolUseId = toolUseId;
        ToolName = toolName;
        StartedUtc = DateTime.UtcNow;
        Command = ToolText.Str(input, "command") ?? "";
        Description = ToolText.Str(input, "description") is { Length: > 0 } d ? d : ToolText.FirstLine(Command) is { Length: > 0 } line ? line : toolName;
    }

    public string ToolUseId { get; }

    /// <summary>"Bash", "PowerShell": the row's chip.</summary>
    public string ToolName { get; }

    /// <summary>The id the CLI gave it ("Command running in background with ID: …"), which its notification names.</summary>
    public string? TaskId { get; set; }

    public DateTime StartedUtc { get; }

    public DateTime? EndedUtc { get; private set; }

    public string Description { get; }

    public string Command { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    public partial bool IsRunning { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    public partial bool IsFailed { get; set; }

    public bool IsDone => !IsRunning && !IsFailed;

    /// <summary>"Running", then the notification's summary ("… completed (exit code 0)").</summary>
    [ObservableProperty]
    public partial string LastStatus { get; set; } = "Running";

    [ObservableProperty]
    public partial string DurationText { get; set; } = "0s";

    public void Finish(bool failed, string? summary)
    {
        if (!IsRunning)
        {
            return;
        }

        EndedUtc = DateTime.UtcNow;
        IsFailed = failed;
        IsRunning = false;
        // "Background command "Build" completed (exit code 0)": the row already names it.
        var said = summary?.Trim() ?? "";
        var prefix = $"Background command \"{Description}\" ";
        if (said.StartsWith(prefix, StringComparison.Ordinal) && said.Length > prefix.Length)
        {
            said = char.ToUpperInvariant(said[prefix.Length]) + said[(prefix.Length + 1)..];
        }

        LastStatus = said.Length > 0 ? said : failed ? "Failed" : "Ended";
        Tick();
    }

    public void Tick()
    {
        var elapsed = (EndedUtc ?? DateTime.UtcNow) - StartedUtc;
        DurationText = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
            : elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
            : $"{elapsed.Seconds}s";
    }
}
