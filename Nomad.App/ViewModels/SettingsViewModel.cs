using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Infrastructure;
using Nomad.Core.Process;
using Nomad.Core.Settings;

namespace Nomad.App.ViewModels;

/// <summary>
/// The settings dialog. Every change is written to <see cref="NomadSettings"/> and saved at once;
/// appearance changes apply live, the Claude Code defaults apply to new sessions.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] PreferredCodeFonts =
        ["Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono", "Fira Code", "Source Code Pro", "Courier New", "Lucida Console"];

    private readonly MainViewModel _main;
    private readonly bool _loaded;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        var s = main.Settings;
        var installed = InstalledFonts();
        // Anthropic Sans and Mono are offered when the Claude desktop app is installed.
        string[] claudeSans = ClaudeFonts.HasSans ? [ClaudeFonts.Sans] : [];
        string[] claudeMono = ClaudeFonts.HasMono ? [ClaudeFonts.Mono] : [];
        UiFonts = Distinct(["Inter", .. claudeSans, "Segoe UI", .. installed], s.InterfaceFont);
        CodeFonts = Distinct([.. claudeMono, .. PreferredCodeFonts.Where(f => installed.Contains(f, StringComparer.OrdinalIgnoreCase)), .. installed], s.CodeFont);

        InterfaceFont = s.InterfaceFont;
        CodeFont = s.CodeFont;
        TextSize = s.TextSize;
        TranscriptWidth = s.TranscriptWidth;
        CliPath = s.CliPath;
        DisplayName = s.DisplayName ?? "";
        DefaultModel = Catalog.Models.FirstOrDefault(m => m.Id == s.DefaultModel) ?? Catalog.Models[0];
        DefaultMode = Catalog.Modes.FirstOrDefault(m => m.Id == s.DefaultPermissionMode) ?? Catalog.Modes[1];
        DefaultEffort = Catalog.Efforts.FirstOrDefault(e => e.Id == s.DefaultEffort) ?? Catalog.Efforts[0];
        UpdateCliStatus();
        _loaded = true;
    }

    public IReadOnlyList<string> UiFonts { get; }

    public IReadOnlyList<string> CodeFonts { get; }

    public IReadOnlyList<ModelOption> Models => Catalog.Models;

    public IReadOnlyList<ModeOption> Modes => Catalog.Modes;

    public IReadOnlyList<EffortOption> Efforts => Catalog.Efforts;

    public string SettingsPath => _main.SettingsPath;

    // ------------------------------------------------------------------ navigation

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClaudeCodePage))]
    public partial bool IsGeneralPage { get; set; } = true;

    public bool IsClaudeCodePage => !IsGeneralPage;

    [RelayCommand]
    private void ShowGeneral() => IsGeneralPage = true;

    [RelayCommand]
    private void ShowClaudeCode() => IsGeneralPage = false;

    // ------------------------------------------------------------------ General › Appearance

    [ObservableProperty]
    public partial string InterfaceFont { get; set; }

    [ObservableProperty]
    public partial string CodeFont { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextSmall), nameof(IsTextMedium), nameof(IsTextLarge))]
    public partial string TextSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWidthNarrow), nameof(IsWidthMedium), nameof(IsWidthWide))]
    public partial string TranscriptWidth { get; set; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    public bool IsTextSmall { get => TextSize == "Small"; set => SetIf(value, () => TextSize = "Small"); }

    public bool IsTextMedium { get => TextSize is not ("Small" or "Large"); set => SetIf(value, () => TextSize = "Medium"); }

    public bool IsTextLarge { get => TextSize == "Large"; set => SetIf(value, () => TextSize = "Large"); }

    public bool IsWidthNarrow { get => TranscriptWidth == "Narrow"; set => SetIf(value, () => TranscriptWidth = "Narrow"); }

    public bool IsWidthMedium { get => TranscriptWidth == "Medium"; set => SetIf(value, () => TranscriptWidth = "Medium"); }

    public bool IsWidthWide { get => TranscriptWidth is not ("Narrow" or "Medium"); set => SetIf(value, () => TranscriptWidth = "Wide"); }

    partial void OnInterfaceFontChanged(string value) => Apply(s => s.InterfaceFont = string.IsNullOrWhiteSpace(value) ? "Inter" : value, appearance: true);

    partial void OnCodeFontChanged(string value) => Apply(s => s.CodeFont = string.IsNullOrWhiteSpace(value) ? "Cascadia Mono" : value, appearance: true);

    partial void OnTextSizeChanged(string value) => Apply(s => s.TextSize = value, appearance: true);

    partial void OnTranscriptWidthChanged(string value) => Apply(s => s.TranscriptWidth = value, appearance: true);

    partial void OnDisplayNameChanged(string value)
    {
        Apply(s => s.DisplayName = string.IsNullOrWhiteSpace(value) ? null : value.Trim(), appearance: false);
        if (_loaded)
        {
            _main.ApplyDisplayName();
        }
    }

    // ------------------------------------------------------------------ Claude Code

    [ObservableProperty]
    public partial string CliPath { get; set; }

    [ObservableProperty]
    public partial string CliStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool CliFound { get; set; }

    [ObservableProperty]
    public partial ModelOption DefaultModel { get; set; }

    [ObservableProperty]
    public partial ModeOption DefaultMode { get; set; }

    [ObservableProperty]
    public partial EffortOption DefaultEffort { get; set; }

    partial void OnCliPathChanged(string value)
    {
        Apply(s => s.CliPath = string.IsNullOrWhiteSpace(value) ? "claude" : value.Trim(), appearance: false);
        UpdateCliStatus();
    }

    partial void OnDefaultModelChanged(ModelOption value) => Apply(s => s.DefaultModel = value.Id, appearance: false);

    partial void OnDefaultModeChanged(ModeOption value) => Apply(s => s.DefaultPermissionMode = value.Id, appearance: false);

    partial void OnDefaultEffortChanged(EffortOption value) => Apply(s => s.DefaultEffort = value.Id, appearance: false);

    private void UpdateCliStatus()
    {
        var resolved = CliLocator.Resolve(string.IsNullOrWhiteSpace(CliPath) ? "claude" : CliPath.Trim());
        CliFound = resolved is not null;
        CliStatus = resolved is not null ? "Found: " + resolved : "Not found on PATH or at that location";
    }

    // ------------------------------------------------------------------ helpers

    private void Apply(Action<NomadSettings> change, bool appearance)
    {
        if (!_loaded)
        {
            return;
        }

        change(_main.Settings);
        if (appearance)
        {
            App.ApplyAppearance(_main.Settings);
        }

        _main.SaveSettings();
    }

    private static void SetIf(bool value, Action set)
    {
        if (value)
        {
            set();
        }
    }

    private static List<string> InstalledFonts()
    {
        try
        {
            return FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('@'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> fonts, string current)
    {
        var list = fonts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrWhiteSpace(current) && !list.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            list.Insert(0, current);
        }

        return list;
    }
}
