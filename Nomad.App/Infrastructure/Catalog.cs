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
        new("claude-fable-5-2", "Fable 5.2", 1),
        new("claude-opus-5-5", "Opus 5.5", 2),
        new("claude-sonnet-5-5", "Sonnet 5.5", 3),
        new("claude-haiku-5-5", "Haiku 5.5", 4),
    ];

    /// <summary>
    /// The CLI's models besides <see cref="Models"/>, by family as the four are ordered, the newest of each first. Its
    /// aliases ("opus", "fable"…) by the full id of the model they name, since some name one it lists no other way.
    /// </summary>
    public static List<Nomad.Core.Settings.KnownModel> OtherModels(IEnumerable<Nomad.Core.Protocol.ModelInfo> models) =>
    [
        .. models
            .Select(Known)
            .OfType<Nomad.Core.Settings.KnownModel>()
            .Where(m => !Models.Any(c => string.Equals(c.Id, m.Id, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(FamilyRank)
            .ThenByDescending(m => Version.TryParse(VersionPart(m.Name), out var v) ? v : new Version()),
    ];

    /// <summary>
    /// A CLI model by its full id: "claude-opus-4-8" as it is, the alias "fable" named "Fable 5.1" as "claude-fable-5-1"
    /// (the CLI's own naming); null for "default" and for an alias whose name has no version.
    /// </summary>
    private static Nomad.Core.Settings.KnownModel? Known(Nomad.Core.Protocol.ModelInfo model)
    {
        if (model.Value is not { } id) return null;

        var name = model.DisplayName ?? ModelName(id);
        if (id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return new() { Id = id, Name = name };

        var parts = name.Split(' ');
        var versioned = parts.Length == 2 && Models.Any(m => m.DisplayName.StartsWith(parts[0] + " ", StringComparison.OrdinalIgnoreCase))
            && parts[1].Split('.').All(p => p.Length > 0 && p.All(char.IsDigit));
        if (!versioned) return null;

        var fullId = $"claude-{parts[0].ToLowerInvariant()}-{parts[1].Replace('.', '-')}";
        return new() { Id = fullId, Name = name };
    }

    /// <summary>
    /// Where a model's family ("Opus" of "Opus 4.8") sits among the four, or after them all.
    /// </summary>
    private static int FamilyRank(Nomad.Core.Settings.KnownModel model)
    {
        var family = model.Name.Split(' ')[0];
        var index = Models.ToList().FindIndex(m => m.DisplayName.StartsWith(family + " ", StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : Models.Count;
    }

    /// <summary>
    /// "4.8" of "Opus 4.8", "5.0" of "Fable 5" (Version needs a minor).
    /// </summary>
    private static string VersionPart(string name)
    {
        var parts = name.Split(' ');
        if (parts.Length < 2) return "";

        return parts[1].Contains('.') ? parts[1] : parts[1] + ".0";
    }

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

        // Codex's: its name in Codex's catalogue, else "GPT-6.1-Sol" from "gpt-6.1-sol".
        if (CodexCatalog.IsCodexModel(id))
        {
            return CodexCatalog.Find(id)?.Name ?? (id.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
                ? "GPT" + string.Join("-", id[3..].Split('-').Select(p => p.Length > 0 && char.IsLetter(p[0]) ? Format.Capitalize(p) : p))
                : id);
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

        // Other ids ("claude-haiku-4-5-20251001", "claude-fable-5") and the aliases an Agent call names ("sonnet"):
        // "Haiku 4.5", "Fable 5", "Sonnet". A minor version has one or two digits; more are a date.
        var named = System.Text.RegularExpressions.Regex.Match(id, @"^(?:claude-)?([a-z]+)(?:-(\d+)(?:-(\d{1,2})(?!\d))?)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (named.Success)
        {
            var family = Format.Capitalize(named.Groups[1].Value.ToLowerInvariant());
            var version = named.Groups[3].Success ? $"{named.Groups[2].Value}.{named.Groups[3].Value}" : named.Groups[2].Value;
            return version.Length > 0 ? $"{family} {version}" : family;
        }

        return id;
    }

    /// <summary>Context window used for the status-row ring (Opus 5.5: 500k as in the original's panel).</summary>
    public static int ContextWindow(string? model) => model switch
    {
        null => 200_000,
        _ when OpenCodeCatalog.Find(model) is { ContextWindow: > 0 } m => m.ContextWindow,
        _ when Nomad.Core.Codex.CodexServer.ContextWindows.TryGetValue(model, out var window) => (int)window,
        _ when CodexCatalog.IsCodexModel(model) => 272_000,
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

    /// <summary>What a mode does in a Codex session (its approval policy and sandbox, CodexBackend.Policy).</summary>
    public static string? CodexModeDescription(string id) => id switch
    {
        "auto" => "Codex's reviewer decides what it asks for",
        "default" => "Read freely; ask before any change",
        "acceptEdits" => "Edits and commands in the folder without asking",
        "plan" => "Plans without editing (Codex's plan mode)",
        "bypassPermissions" => "Run anything, unsandboxed, without asking",
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
