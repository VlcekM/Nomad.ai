using Nomad.Core.OpenCode;

namespace Nomad.App.Infrastructure;

/// <summary>OpenCode's models, as its server lists them (the connected providers'): loaded once OpenCode is in use.</summary>
public static class OpenCodeCatalog
{
    private static Dictionary<string, OpenCodeModel> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>By provider, then name.</summary>
    public static IReadOnlyList<OpenCodeModel> Models { get; private set; } = [];

    /// <summary>OpenCode's own default model ("provider/model"), if it names one.</summary>
    public static string? DefaultModel { get; private set; }

    public static void Set(IReadOnlyList<OpenCodeModel> models, string? defaultModel)
    {
        Models = [.. models.OrderBy(m => m.ProviderName, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
        _byId = models.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        DefaultModel = defaultModel is not null && _byId.ContainsKey(defaultModel) ? defaultModel : Models.FirstOrDefault()?.Id;
    }

    public static OpenCodeModel? Find(string? id) => id is not null && _byId.TryGetValue(id, out var m) ? m : null;
}
