using Nomad.Core.Settings;

namespace Nomad.App.Infrastructure;

/// <summary>
/// Built-in preset themes besides Nomad (none; only the default Nomad theme is built-in).
/// </summary>
public static class ThemePresets
{
    public static IReadOnlyList<(string Id, CustomTheme Theme)> All { get; } = [];

    /// <summary>The preset with this id; null for Nomad itself or a theme of the user's.</summary>
    public static CustomTheme? Find(string id) => null;
}
