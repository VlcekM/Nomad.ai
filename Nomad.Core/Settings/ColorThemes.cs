using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nomad.Core.Settings;

/// <summary>
/// A color theme of the user's: palette tokens ("BgMain", "TextPrimary", "Accent") to colors ("#RRGGBB" or
/// "#AARRGGBB"), for the dark and the light mode. Tokens it leaves out keep the built-in theme's colors.
/// </summary>
public sealed class CustomTheme
{
    public string Name { get; set; } = "";

    public Dictionary<string, string> Dark { get; set; } = [];

    public Dictionary<string, string> Light { get; set; } = [];
}

/// <summary>
/// Loads and saves <see cref="CustomTheme"/>s, one JSON file each in a themes folder (next to settings.json). A
/// theme's id is its file name; the built-in theme has none and is never a file.
/// </summary>
public static class ColorThemeStore
{
    public static IReadOnlyList<(string Id, CustomTheme Theme)> LoadAll(string folder)
    {
        var themes = new List<(string, CustomTheme)>();
        try
        {
            if (!Directory.Exists(folder))
            {
                return themes;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (Load(folder, id) is { } theme)
                {
                    themes.Add((id, theme));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder lists no themes.
        }

        return themes.OrderBy(t => t.Item2.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static CustomTheme? Load(string folder, string id)
    {
        try
        {
            var path = PathOf(folder, id);
            if (!File.Exists(path))
            {
                return null;
            }

            var theme = FromJson(File.ReadAllText(path));
            if (theme is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(theme.Name))
            {
                theme.Name = id;
            }

            return theme;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static void Save(string folder, string id, CustomTheme theme)
    {
        Directory.CreateDirectory(folder);
        var path = PathOf(folder, id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(theme));
        File.Move(temp, path, overwrite: true);
    }

    public static string ToJson(CustomTheme theme) => JsonSerializer.Serialize(theme, ColorThemeJsonContext.Default.CustomTheme);

    /// <summary>A theme read from JSON (a file of its own or an exported one); null if it isn't one.</summary>
    public static CustomTheme? FromJson(string json)
    {
        try
        {
            var theme = JsonSerializer.Deserialize(json, ColorThemeJsonContext.Default.CustomTheme);
            if (theme is null)
            {
                return null;
            }

            theme.Name ??= "";
            theme.Dark ??= [];
            theme.Light ??= [];
            return theme;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string PathOf(string folder, string id) => Path.Combine(folder, id + ".json");

    /// <summary>A file name for a new theme called <paramref name="name"/>, not taken in the folder nor by <paramref name="reserved"/>.</summary>
    public static string NewId(string folder, string name, string reserved)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(name.Trim().ToLowerInvariant().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).Trim('-', '.');
        if (stem.Length == 0)
        {
            stem = "theme";
        }

        var id = stem;
        for (var n = 2; string.Equals(id, reserved, StringComparison.OrdinalIgnoreCase) || File.Exists(PathOf(folder, id)); n++)
        {
            id = $"{stem}-{n}";
        }

        return id;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CustomTheme))]
internal sealed partial class ColorThemeJsonContext : JsonSerializerContext;
