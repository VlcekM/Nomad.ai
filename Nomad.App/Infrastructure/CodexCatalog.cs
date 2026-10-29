using Nomad.Core.Codex;

namespace Nomad.App.Infrastructure;

/// <summary>Codex's models, as its server lists them for the account: loaded once Codex is in use.</summary>
public static class CodexCatalog
{
    private static Dictionary<string, CodexModel> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>In Codex's order.</summary>
    public static IReadOnlyList<CodexModel> Models { get; private set; } = [];

    /// <summary>The model Codex marks as its default, else its first.</summary>
    public static string? DefaultModel { get; private set; }

    public static void Set(IReadOnlyList<CodexModel> models)
    {
        Models = models;
        _byId = models.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        DefaultModel = models.FirstOrDefault(m => m.IsDefault)?.Id ?? models.FirstOrDefault()?.Id;
    }

    public static CodexModel? Find(string? id) => id is not null && _byId.TryGetValue(id, out var m) ? m : null;

    /// <summary>A Codex model id ("gpt-…", "o3", "codex-…"), as opposed to Claude's or OpenCode's.</summary>
    public static bool IsCodexModel(string? id) =>
        Find(id) is not null
        || (id is { Length: > 0 } && !id.Contains('/')
            && System.Text.RegularExpressions.Regex.IsMatch(id, @"^(gpt|o\d|codex)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
}
