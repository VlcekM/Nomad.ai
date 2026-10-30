using Nomad.Core.Settings;

namespace Nomad.App.Infrastructure;

/// <summary>
/// The built-in themes besides Nomad: Nomad's own surfaces with other accent (primary) and focus (secondary) colors,
/// and themes of their own after well-known editor palettes. Like Nomad they can't be changed, only duplicated; each
/// sets the base colors (<see cref="Themes.Tokens"/>) it differs in, and the rest follow them as in a custom theme.
/// </summary>
public static class ThemePresets
{
    public static IReadOnlyList<(string Id, CustomTheme Theme)> All { get; } =
    [
        // Nomad with other accents: its neutral surfaces, a new primary and secondary.
        ("nomad-ocean", Accents("Nomad Ocean", ("#4C9BE0", "#2BB3A3"), ("#2F7BC4", "#1F8F84"))),
        ("nomad-forest", Accents("Nomad Forest", ("#6AAE6F", "#3E9BB5"), ("#3E8A4A", "#2E7F96"))),
        ("nomad-violet", Accents("Nomad Violet", ("#A483F2", "#E07AB0"), ("#7A55D6", "#C2508E"))),
        ("nomad-rose", Accents("Nomad Rose", ("#E07A92", "#8C7CF0"), ("#C9506D", "#6A5AD8"))),
        ("nomad-amber", Accents("Nomad Amber", ("#E0A33B", "#4F8FE0"), ("#B07510", "#2F6FC7"))),

        // Themes of their own.
        ("nord", Theme("Nord",
            dark: ["#272C36", "#2E3440", "#3B4252", "#3B4252", "#434C5E", "#4C566A", "#ECEFF4", "#9AA3B5",
                   "#88C0D0", "#81A1C1", "#A3BE8C", "#EBCB8B", "#BF616A", "#B48EAD",
                   "#81A1C1", "#8FBCBB", "#88C0D0", "#A3BE8C", "#B48EAD", "#616E88", "#81A1C1", "#8FBCBB"],
            light: ["#E5E9F0", "#ECEFF4", "#E5E9F0", "#FFFFFF", "#D8DEE9", "#CBD2DE", "#2E3440", "#616E88",
                    "#5E81AC", "#4C8A9C", "#5E8A4A", "#B88A1E", "#BF616A", "#9A6C94",
                    "#5E81AC", "#3B7E8A", "#2E7A9A", "#5E8A4A", "#9A6C94", "#7B88A1", "#5E81AC", "#3B7E8A"])),
        ("solarized", Theme("Solarized",
            dark: ["#00232C", "#002B36", "#073642", "#073642", "#0B4250", "#1A4D5A", "#EEE8D5", "#839496",
                   "#268BD2", "#2AA198", "#859900", "#B58900", "#DC322F", "#6C71C4",
                   "#859900", "#B58900", "#268BD2", "#2AA198", "#D33682", "#586E75", "#268BD2", "#6C71C4"],
            light: ["#EEE8D5", "#FDF6E3", "#F5EEDA", "#FFFBEF", "#E6DFC8", "#DDD6C1", "#073642", "#657B83",
                    "#268BD2", "#2AA198", "#859900", "#B58900", "#DC322F", "#6C71C4",
                    "#859900", "#B58900", "#268BD2", "#2AA198", "#D33682", "#93A1A1", "#268BD2", "#6C71C4"])),
        ("gruvbox", Theme("Gruvbox",
            dark: ["#1D2021", "#282828", "#32302F", "#3C3836", "#45403D", "#504945", "#EBDBB2", "#A89984",
                   "#FE8019", "#83A598", "#B8BB26", "#FABD2F", "#FB4934", "#D3869B",
                   "#FB4934", "#FABD2F", "#8EC07C", "#B8BB26", "#D3869B", "#928374", "#83A598", "#8EC07C"],
            light: ["#F2E5BC", "#FBF1C7", "#F4E8BE", "#F9F5D7", "#EBDBB2", "#D5C4A1", "#3C3836", "#7C6F64",
                    "#AF3A03", "#076678", "#79740E", "#B57614", "#9D0006", "#8F3F71",
                    "#9D0006", "#B57614", "#427B58", "#79740E", "#8F3F71", "#928374", "#076678", "#427B58"])),
        ("catppuccin", Theme("Catppuccin",
            dark: ["#181825", "#1E1E2E", "#313244", "#313244", "#45475A", "#3E4056", "#CDD6F4", "#9399B2",
                   "#CBA6F7", "#89B4FA", "#A6E3A1", "#F9E2AF", "#F38BA8", "#F5C2E7",
                   "#CBA6F7", "#F9E2AF", "#89B4FA", "#A6E3A1", "#FAB387", "#6C7086", "#89B4FA", "#F9E2AF"],
            light: ["#E6E9EF", "#EFF1F5", "#E6E9EF", "#FFFFFF", "#DCE0E8", "#CCD0DA", "#4C4F69", "#6C6F85",
                    "#8839EF", "#1E66F5", "#40A02B", "#DF8E1D", "#D20F39", "#EA76CB",
                    "#8839EF", "#DF8E1D", "#1E66F5", "#40A02B", "#FE640B", "#9CA0B0", "#1E66F5", "#DF8E1D"])),
        ("tokyo-night", Theme("Tokyo Night",
            dark: ["#16161E", "#1A1B26", "#24283B", "#1F2335", "#292E42", "#3B4261", "#C0CAF5", "#7A82AE",
                   "#7AA2F7", "#7DCFFF", "#9ECE6A", "#E0AF68", "#F7768E", "#BB9AF7",
                   "#BB9AF7", "#2AC3DE", "#7AA2F7", "#9ECE6A", "#FF9E64", "#565F89", "#F7768E", "#BB9AF7"],
            light: ["#D0D5E3", "#E1E2E7", "#D8DAE3", "#ECEDF1", "#C4C8DA", "#B9BED2", "#343B58", "#6172B0",
                    "#2E7DE9", "#007197", "#587539", "#8C6C3E", "#F52A65", "#9854F1",
                    "#9854F1", "#007197", "#2E7DE9", "#587539", "#B15C00", "#848CB5", "#F52A65", "#9854F1"])),
    ];

    /// <summary>The preset with this id; null for Nomad itself or a theme of the user's.</summary>
    public static CustomTheme? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)).Theme;

    private static CustomTheme Accents(string name, (string Accent, string Focus) dark, (string Accent, string Focus) light) => new()
    {
        Name = name,
        Dark = new() { ["Accent"] = dark.Accent, ["Ring"] = dark.Focus },
        Light = new() { ["Accent"] = light.Accent, ["Ring"] = light.Focus },
    };

    // Colors in the order of Themes.Tokens.
    private static CustomTheme Theme(string name, string[] dark, string[] light) => new()
    {
        Name = name,
        Dark = Themes.Tokens.Zip(dark).ToDictionary(p => p.First.Key, p => p.Second),
        Light = Themes.Tokens.Zip(light).ToDictionary(p => p.First.Key, p => p.Second),
    };
}
