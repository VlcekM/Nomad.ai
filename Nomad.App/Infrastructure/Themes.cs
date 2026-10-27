using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Nomad.Core.Settings;

namespace Nomad.App.Infrastructure;

/// <summary>A palette token a theme can recolor ("BgMain" for BgMainBrush and BgMainColor), as the editor lists it.</summary>
public sealed record ThemeToken(string Key, string Group, string Label);

/// <summary>
/// Color themes. The built-in one, Nomad, is Theme/Palette.axaml itself and can be neither changed nor deleted. A
/// custom theme is laid over it as one more merged dictionary, with the dark and light colors it sets; whatever it
/// leaves out falls through to the palette.
/// </summary>
public static class Themes
{
    public const string DefaultId = "nomad";
    public const string DefaultName = "Nomad";

    // Keys a token also sets: Fluent's combo box brushes, and the main background's transparent twin (edge fades).
    private static readonly Dictionary<string, string[]> Derived = new()
    {
        ["BgHover"] = ["ComboBoxBackgroundPointerOver"],
        ["BgPressed"] = ["ComboBoxBackgroundPressed"],
        ["BgMenu"] = ["ComboBoxDropDownBackground"],
        ["Border"] = ["ComboBoxDropDownBorderBrush"],
        ["TextPrimary"] = ["ComboBoxForeground"],
        ["TextSecondary"] = ["ComboBoxDropDownGlyphForeground"],
    };

    private static readonly string[] ControlTokens =
        ["BarTrack", "RingTrack", "Skeleton", "SliderTick", "SwitchOff", "ScrollThumb", "ScrollThumbHover", "Pace"];

    private static ResourceDictionary? _overlay;
    private static IReadOnlyList<ThemeToken>? _tokens;

    public static string Folder => Path.Combine(Path.GetDirectoryName(Program.SettingsPath)!, "themes");

    /// <summary>Every token, in the palette's order, grouped as the editor shows them.</summary>
    public static IReadOnlyList<ThemeToken> Tokens => _tokens is { Count: > 0 } ? _tokens : _tokens = ReadTokens();

    /// <summary>The built-in color of a token in a mode.</summary>
    public static Color DefaultColor(string token, ThemeVariant variant) =>
        Palette(variant) is { } palette && palette.TryGetResource(token + "Brush", null, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : Colors.Transparent;

    /// <summary>The built-in colors of every token in a mode, as a theme file writes them.</summary>
    public static Dictionary<string, string> DefaultColors(ThemeVariant variant) =>
        Tokens.ToDictionary(t => t.Key, t => Hex(DefaultColor(t.Key, variant)));

    /// <summary>The theme in use, by id (a missing file is the built-in theme).</summary>
    public static string AppliedId { get; private set; } = DefaultId;

    /// <summary>Switches to the theme with this id, read from its file. False if it is the one in use.</summary>
    public static bool Use(string? id)
    {
        id = string.IsNullOrWhiteSpace(id) ? DefaultId : id;
        if (id == AppliedId)
        {
            return false;
        }

        return Apply(id, id == DefaultId ? null : ColorThemeStore.Load(Folder, id));
    }

    /// <summary>Lays <paramref name="theme"/> over the palette (null: the built-in theme alone). False if nothing changed.</summary>
    public static bool Apply(string id, CustomTheme? theme)
    {
        AppliedId = id;
        if (Application.Current is not { } app)
        {
            return false;
        }

        var merged = app.Resources.MergedDictionaries;
        if (theme is null)
        {
            if (_overlay is null)
            {
                return false;
            }

            merged.Remove(_overlay);
            _overlay = null;
            return true;
        }

        var overlay = new ResourceDictionary();
        overlay.ThemeDictionaries[ThemeVariant.Dark] = Variant(theme.Dark);
        overlay.ThemeDictionaries[ThemeVariant.Light] = Variant(theme.Light);
        var index = _overlay is null ? -1 : merged.IndexOf(_overlay);
        if (index >= 0)
        {
            merged[index] = overlay;
        }
        else
        {
            merged.Add(overlay);
        }

        _overlay = overlay;
        return true;
    }

    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        return text.Length is 7 or 9 && text.Skip(1).All(char.IsAsciiHexDigit) && Color.TryParse(text, out color);
    }

    public static string Hex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static ResourceDictionary Variant(Dictionary<string, string> colors)
    {
        var dictionary = new ResourceDictionary();
        foreach (var (token, text) in colors)
        {
            if (!TryParse(text, out var color) || !Tokens.Any(t => t.Key == token))
            {
                continue;
            }

            dictionary[token + "Brush"] = new SolidColorBrush(color);
            dictionary[token + "Color"] = color;
            if (token == "BgMain")
            {
                dictionary["BgMainClearBrush"] = new SolidColorBrush(Color.FromArgb(0, color.R, color.G, color.B));
                dictionary["BgMainClearColor"] = Color.FromArgb(0, color.R, color.G, color.B);
            }

            foreach (var key in Derived.GetValueOrDefault(token, []))
            {
                dictionary[key] = new SolidColorBrush(color);
            }
        }

        return dictionary;
    }

    private static IResourceProvider? Palette(ThemeVariant variant)
    {
        // Palette.axaml: the merged dictionary with theme dictionaries (compiled XAML merges it in itself, not as a
        // ResourceInclude), other than the overlay.
        var palette = Application.Current?.Resources.MergedDictionaries
            .Select(d => d is ResourceInclude include ? include.Loaded : d)
            .OfType<ResourceDictionary>()
            .FirstOrDefault(d => !ReferenceEquals(d, _overlay) && d.ThemeDictionaries.Count > 0);
        return palette?.ThemeDictionaries.TryGetValue(variant, out var provider) == true ? provider : null;
    }

    private static IReadOnlyList<ThemeToken> ReadTokens()
    {
        if (Palette(ThemeVariant.Dark) is not ResourceDictionary dark)
        {
            return [];
        }

        var derived = Derived.Values.SelectMany(k => k).ToHashSet();
        return dark.Keys.OfType<string>()
            .Where(k => k.EndsWith("Brush", StringComparison.Ordinal) && k != "BgMainClearBrush" && !derived.Contains(k))
            .Select(k => k[..^"Brush".Length])
            .Select(k => new ThemeToken(k, GroupOf(k), LabelOf(k)))
            .OrderBy(t => Array.IndexOf(Groups, t.Group))
            .ToList();
    }

    private static readonly string[] Groups = ["Surfaces", "Lines", "Text", "Accents and states", "Controls", "GitHub"];

    private static string GroupOf(string token) => token switch
    {
        _ when token.StartsWith("Bg", StringComparison.Ordinal) => "Surfaces",
        _ when token.StartsWith("Border", StringComparison.Ordinal) || token == "InlineCodeBorder" => "Lines",
        _ when token.StartsWith("Text", StringComparison.Ordinal) => "Text",
        _ when token.StartsWith("Gh", StringComparison.Ordinal) => "GitHub",
        _ when ControlTokens.Contains(token) || token.StartsWith("Jump", StringComparison.Ordinal) || token.StartsWith("Shimmer", StringComparison.Ordinal) => "Controls",
        _ => "Accents and states",
    };

    // "BgNavSelected" → "Nav selected", "DiffAddBg" → "Diff add background", "Border" → "Border".
    private static string LabelOf(string token)
    {
        var words = System.Text.RegularExpressions.Regex.Split(token, "(?<=[a-z])(?=[A-Z])").ToList();
        if (words.Count > 1 && words[0] is "Bg" or "Border" or "Text" or "Gh")
        {
            words.RemoveAt(0);
        }

        words = words.Select(w => w == "Bg" ? "background" : w.ToLowerInvariant()).ToList();
        var label = string.Join(' ', words);
        return char.ToUpperInvariant(label[0]) + label[1..];
    }
}
