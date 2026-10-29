namespace Nomad.App.Infrastructure;

public sealed record ModelOption(string Id, string DisplayName, int Shortcut);

public sealed record ModeOption(string Id, string DisplayName, string Description, int Shortcut);

public sealed record EffortOption(string Id, string DisplayName);

/// <summary>The fixed menus of v1: models, permission modes and effort levels.</summary>
public static class Catalog
{
    /// <summary>The latest model of each family, most capable first (the status-row chips and Ctrl+Shift+1…4).</summary>
    public static readonly IReadOnlyList<ModelOption> Models =
    [
        new("claude-fable-5-1", "Fable 5.1", 1),
        new("claude-opus-5-5", "Opus 5.5", 2),
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

    /// <summary>The effort slider's stops, left to right (Efforts without "Default").</summary>
    public static readonly IReadOnlyList<EffortOption> EffortLevels = [.. Efforts.Where(e => e.Id.Length > 0)];

    public static string ModelName(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "Default model";
        }

        // OpenCode's "provider/model": its name in OpenCode's catalogue, else the model part.
        if (Nomad.Core.OpenCode.OpenCodeClient.SplitModel(id) is var (_, model))
        {
            return OpenCodeCatalog.Find(id)?.Name ?? model;
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

        // Other ids ("claude-haiku-4-5-20251001") and the aliases an Agent call names ("sonnet"): "Haiku 4.5", "Sonnet".
        var named = System.Text.RegularExpressions.Regex.Match(id, @"^(?:claude-)?([a-z]+)(?:-(\d+)-(\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (named.Success)
        {
            var family = Format.Capitalize(named.Groups[1].Value.ToLowerInvariant());
            return named.Groups[2].Success ? $"{family} {named.Groups[2].Value}.{named.Groups[3].Value}" : family;
        }

        return id;
    }

    /// <summary>Context window used for the status-row ring (Opus 5.5: 500k as in the original's panel).</summary>
    public static int ContextWindow(string? model) => model switch
    {
        null => 200_000,
        _ when OpenCodeCatalog.Find(model) is { ContextWindow: > 0 } m => m.ContextWindow,
        _ when model.Contains("[1m]", StringComparison.OrdinalIgnoreCase) => 1_000_000,
        _ when model.StartsWith("claude-opus-5", StringComparison.OrdinalIgnoreCase) => 500_000,
        _ => 200_000,
    };

    /// <summary>What a mode does in an OpenCode session, where it differs from Claude Code's wording.</summary>
    public static string? OpenCodeModeDescription(string id) => id switch
    {
        "default" => "Ask before edits, commands and web fetches",
        "acceptEdits" => "Edit files without asking; ask before commands",
        "plan" => "Plans without editing (OpenCode's plan agent)",
        "bypassPermissions" => "Allow everything without asking",
        _ => null,
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

        // OpenCode's other variant names ("none", "minimal", "thinking"…).
        return Format.Capitalize(id ?? "");
    }
}
