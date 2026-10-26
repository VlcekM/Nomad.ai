namespace Nomad.App.Infrastructure;

public sealed record ModelOption(string Id, string DisplayName, int Shortcut);

public sealed record ModeOption(string Id, string DisplayName, string Description, int Shortcut);

public sealed record EffortOption(string Id, string DisplayName);

/// <summary>The fixed menus of v1: models, permission modes and effort levels.</summary>
public static class Catalog
{
    public static readonly IReadOnlyList<ModelOption> Models =
    [
        new("claude-opus-5-5", "Opus 5.5", 1),
        new("claude-fable-5-1", "Fable 5.1", 2),
        new("claude-sonnet-5-5", "Sonnet 5.5", 3),
        new("claude-haiku-5-5", "Haiku 5.5", 4),
    ];

    public static readonly IReadOnlyList<ModeOption> Modes =
    [
        new("auto", "Auto", "Claude handles permission decisions", 1),
        new("default", "Manual", "Always ask before making changes", 2),
        new("acceptEdits", "Accept edits", "Automatically accept all file edits", 3),
        new("plan", "Plan", "Create a plan before making changes", 4),
        new("bypassPermissions", "Bypass permissions", "Skip all permission prompts", 5),
    ];

    public static readonly IReadOnlyList<EffortOption> Efforts =
    [
        new("", "Default"),
        new("low", "Low"),
        new("medium", "Medium"),
        new("high", "High"),
        new("xhigh", "Extra high"),
        new("max", "Max"),
    ];

    public static string ModelName(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "Default model";
        }

        foreach (var m in Models)
        {
            if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return m.DisplayName;
            }
        }

        // e.g. "claude-opus-5-5[1m]" or dated ids: match by prefix.
        foreach (var m in Models)
        {
            if (id.StartsWith(m.Id, StringComparison.OrdinalIgnoreCase))
            {
                return m.DisplayName;
            }
        }

        return id;
    }

    /// <summary>Context window used for the status-row ring (Opus 5.5: 500k as in the original's panel).</summary>
    public static int ContextWindow(string? model) => model switch
    {
        null => 200_000,
        _ when model.Contains("[1m]", StringComparison.OrdinalIgnoreCase) => 1_000_000,
        _ when model.StartsWith("claude-opus-5", StringComparison.OrdinalIgnoreCase) => 500_000,
        _ => 200_000,
    };

    public static string ModeName(string? id)
    {
        foreach (var m in Modes)
        {
            if (m.Id == id)
            {
                return m.DisplayName;
            }
        }

        return id switch
        {
            "dontAsk" => "Don't ask",
            null or "" => "Manual",
            _ => id,
        };
    }

    public static string EffortName(string? id)
    {
        foreach (var e in Efforts)
        {
            if (e.Id == (id ?? ""))
            {
                return e.DisplayName;
            }
        }

        return id ?? "";
    }
}
