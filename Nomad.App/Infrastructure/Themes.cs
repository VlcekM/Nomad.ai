using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Nomad.Core.Settings;

namespace Nomad.App.Infrastructure;

/// <summary>A base color a theme can set ("BgMain" for BgMainBrush and BgMainColor), as the editor lists it.</summary>
public sealed record ThemeToken(string Key, string Group, string Label);

/// <summary>
/// Color themes. The built-in one, Nomad, is Theme/Palette.axaml itself and can be neither changed nor deleted. A
/// custom theme is laid over it as one more merged dictionary, with the dark and light colors it sets; whatever it
/// leaves out falls through to the palette.
/// <para>
/// A theme sets only a few base colors (<see cref="Tokens"/>); the palette's other tokens follow them. Each of those
/// sits between two base colors (a button between the raised surface and the text, say) at the spot the palette puts
/// it, and moves with them by as much as that spot does. With Nomad's own base colors it is exactly the palette's, and
/// under any theme every surface, line and state keeps a shade of its own.
/// </para>
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

    // The palette tokens that follow the base colors, each with the two it sits between (the same one twice: it
    // shifts with that color and keeps its own alpha). Tokens left out (the dim overlay, the caption's close hover)
    // stay Nomad's under any theme.
    private static readonly Dictionary<string, (string From, string To)> Followers = new()
    {
        ["BgHover"] = ("TextPrimary", "TextPrimary"),
        ["BgPressed"] = ("TextPrimary", "TextPrimary"),
        ["BgSearch"] = ("BgInput", "BgMain"),
        ["BgCode"] = ("BgMain", "BgRaised"),
        ["BgChip"] = ("BgRaised", "TextPrimary"),
        ["BgMenu"] = ("BgInput", "BgInput"),
        ["BgButton"] = ("BgRaised", "TextPrimary"),
        ["BgButtonHover"] = ("BgRaised", "TextPrimary"),
        ["BgSegments"] = ("BgMain", "TextPrimary"),
        ["BgSegmentChecked"] = ("BgInput", "TextPrimary"),
        ["BgNavSelected"] = ("BgSelected", "TextPrimary"),
        ["BgField"] = ("BgInput", "BgInput"),
        ["BgSettings"] = ("BgMain", "TextPrimary"),
        ["BorderInput"] = ("Border", "TextPrimary"),
        ["BorderCard"] = ("Border", "BgMain"),
        ["BorderDivider"] = ("Border", "BgMain"),
        ["BorderSubtle"] = ("TextPrimary", "TextPrimary"),
        ["BorderField"] = ("Border", "TextPrimary"),
        ["BorderSettings"] = ("Border", "BgMain"),
        ["InlineCodeBorder"] = ("Border", "TextPrimary"),
        ["TextChrome"] = ("TextPrimary", "TextMuted"),
        ["TextSecondary"] = ("TextPrimary", "TextMuted"),
        ["TextNav"] = ("TextPrimary", "TextMuted"),
        ["TextOnAccent"] = ("BgMain", "BgMain"),
        ["TextCaret"] = ("TextPrimary", "TextPrimary"),
        ["TextSelection"] = ("Ring", "Ring"),
        ["AccentEmphasis"] = ("Accent", "BgMain"),
        ["AccentSubtle"] = ("Accent", "Accent"),
        ["InlineCode"] = ("Accent", "TextPrimary"),
        ["DiffAdd"] = ("Success", "TextPrimary"),
        ["DiffAddBg"] = ("Success", "BgMain"),
        ["DiffDel"] = ("Danger", "TextPrimary"),
        ["DiffDelBg"] = ("Danger", "BgMain"),
        ["DangerSubtle"] = ("Danger", "Danger"),
        ["Link"] = ("Ring", "TextPrimary"),
        ["LinkSubtle"] = ("Ring", "Ring"),
        ["Unread"] = ("Ring", "Ring"),
        ["Check"] = ("Ring", "Ring"),
        ["Pace"] = ("Ring", "Ring"),
        ["BarTrack"] = ("BgRaised", "TextPrimary"),
        ["SwitchOff"] = ("BgRaised", "TextPrimary"),
        ["RingTrack"] = ("TextPrimary", "TextPrimary"),
        ["Skeleton"] = ("TextPrimary", "TextPrimary"),
        ["SliderTick"] = ("TextPrimary", "TextPrimary"),
        ["ScrollThumb"] = ("TextPrimary", "TextPrimary"),
        ["ScrollThumbHover"] = ("TextPrimary", "TextPrimary"),
        ["JumpFill"] = ("BgRaised", "TextPrimary"),
        ["JumpEdge"] = ("Border", "TextPrimary"),
        ["JumpArrow"] = ("TextPrimary", "TextMuted"),
        ["ShimmerBase"] = ("TextMuted", "TextPrimary"),
        ["ShimmerHighlight"] = ("TextPrimary", "TextPrimary"),
        ["GhOpen"] = ("Success", "TextPrimary"),
        ["GhClosed"] = ("Danger", "TextPrimary"),
        ["GhNeutral"] = ("TextMuted", "TextMuted"),
    };

    private static ResourceDictionary? _overlay;

    public static string Folder => Path.Combine(Path.GetDirectoryName(Program.SettingsPath)!, "themes");

    /// <summary>The base colors a theme sets, grouped as the editor shows them.</summary>
    public static IReadOnlyList<ThemeToken> Tokens { get; } =
    [
        new("BgSidebar", "Surfaces", "Sidebar"),
        new("BgMain", "Surfaces", "Background"),
        new("BgRaised", "Surfaces", "Raised"),
        new("BgInput", "Surfaces", "Input and menus"),
        new("BgSelected", "Surfaces", "Selected"),
        new("Border", "Lines and text", "Border"),
        new("TextPrimary", "Lines and text", "Text"),
        new("TextMuted", "Lines and text", "Muted text"),
        new("Accent", "Accents and states", "Accent"),
        new("Ring", "Accents and states", "Focus"),
        new("Success", "Accents and states", "Success"),
        new("Waiting", "Accents and states", "Waiting"),
        new("Danger", "Accents and states", "Danger"),
        new("GhMerged", "Accents and states", "Merged"),
    ];

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
        overlay.ThemeDictionaries[ThemeVariant.Dark] = Variant(theme.Dark, ThemeVariant.Dark);
        overlay.ThemeDictionaries[ThemeVariant.Light] = Variant(theme.Light, ThemeVariant.Light);
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

    // One mode of a theme: its base colors, and the tokens that follow the ones it changed. Colors of tokens that
    // aren't base colors (from themes saved before there were base colors) are left out.
    private static ResourceDictionary Variant(Dictionary<string, string> colors, ThemeVariant variant)
    {
        var dictionary = new ResourceDictionary();
        var moved = new Dictionary<string, (int R, int G, int B)>();
        foreach (var token in Tokens)
        {
            var @default = DefaultColor(token.Key, variant);
            var color = TryParse(colors.GetValueOrDefault(token.Key), out var own) ? own : @default;
            moved[token.Key] = (color.R - @default.R, color.G - @default.G, color.B - @default.B);
            if (color != @default)
            {
                Set(dictionary, token.Key, color);
            }
        }

        foreach (var (token, (from, to)) in Followers)
        {
            var (a, b) = (moved[from], moved[to]);
            if (a == default && b == default)
            {
                continue;
            }

            // How far along the line from one base color to the other the palette puts the token, and so how much of
            // each one's move it takes.
            var c = DefaultColor(token, variant);
            var at = Along(c, DefaultColor(from, variant), DefaultColor(to, variant));
            Set(dictionary, token, Color.FromArgb(c.A,
                Channel(c.R + a.R + (b.R - a.R) * at),
                Channel(c.G + a.G + (b.G - a.G) * at),
                Channel(c.B + a.B + (b.B - a.B) * at)));
        }

        return dictionary;
    }

    private static void Set(ResourceDictionary dictionary, string token, Color color)
    {
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

    // The point nearest c on the line from a to b, as a fraction of the way (0 when a and b are one color). Kept
    // near the segment, so a token close to neither end doesn't take a magnified share of their moves.
    private static double Along(Color c, Color a, Color b)
    {
        double dr = b.R - a.R, dg = b.G - a.G, db = b.B - a.B;
        var length = dr * dr + dg * dg + db * db;
        return length == 0 ? 0 : Math.Clamp(((c.R - a.R) * dr + (c.G - a.G) * dg + (c.B - a.B) * db) / length, -1, 2);
    }

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

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
}
