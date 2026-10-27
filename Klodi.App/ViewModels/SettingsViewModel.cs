using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klodi.App.Infrastructure;
using Klodi.App.Updates;
using Klodi.Core.Process;
using Klodi.Core.Settings;
using Klodi.Core.Updates;

namespace Klodi.App.ViewModels;

public enum SettingsPageKind
{
    General,
    ClaudeCode,
    Updates,
}

/// <summary>
/// The settings dialog. Every change is written to <see cref="KlodiSettings"/> and saved at once;
/// appearance changes apply live, the Claude Code defaults apply to new sessions.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] PreferredCodeFonts =
        ["Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono", "Fira Code", "Source Code Pro", "Courier New", "Lucida Console"];

    private readonly MainViewModel _main;
    private readonly bool _loaded;
    private ReleaseList? _releaseList;
    private bool _releasesRequested;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        var s = main.Settings;
        var installed = InstalledFonts();
        UiFonts = Distinct(["Inter", "Segoe UI", .. installed], s.InterfaceFont);
        CodeFonts = Distinct([.. PreferredCodeFonts.Where(f => installed.Contains(f, StringComparer.OrdinalIgnoreCase)), .. installed], s.CodeFont);

        InterfaceFont = s.InterfaceFont;
        CodeFont = s.CodeFont;
        TextSize = s.TextSize;
        TranscriptWidth = s.TranscriptWidth;
        CliPath = s.CliPath;
        DisplayName = s.DisplayName ?? "";
        HardwareAcceleration = s.HardwareAcceleration;
        AutoUpdate = s.AutoUpdate;
        IncludePrereleases = s.IncludePrereleases;
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
    [NotifyPropertyChangedFor(nameof(IsGeneralPage), nameof(IsClaudeCodePage), nameof(IsUpdatesPage))]
    public partial SettingsPageKind Page { get; set; }

    public bool IsGeneralPage => Page == SettingsPageKind.General;

    public bool IsClaudeCodePage => Page == SettingsPageKind.ClaudeCode;

    public bool IsUpdatesPage => Page == SettingsPageKind.Updates;

    [RelayCommand]
    private void ShowGeneral() => Page = SettingsPageKind.General;

    [RelayCommand]
    private void ShowClaudeCode() => Page = SettingsPageKind.ClaudeCode;

    [RelayCommand]
    private void ShowUpdates()
    {
        Page = SettingsPageKind.Updates;
        _ = LoadReleasesAsync();
    }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartNeeded))]
    public partial bool HardwareAcceleration { get; set; }

    /// <summary>The renderer is chosen at startup: say so when the setting differs from this run.</summary>
    public bool RestartNeeded => HardwareAcceleration != Program.HardwareAccelerated;

    [RelayCommand]
    private void ToggleHardwareAcceleration() => HardwareAcceleration = !HardwareAcceleration;

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

    partial void OnHardwareAccelerationChanged(bool value) => Apply(s => s.HardwareAcceleration = value, appearance: false);

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

    // ------------------------------------------------------------------ Updates

    public UpdateService Updates => _main.Updates;

    [ObservableProperty]
    public partial bool AutoUpdate { get; set; }

    [ObservableProperty]
    public partial bool IncludePrereleases { get; set; }

    /// <summary>The repository's releases, newest first (pre-releases only with the setting on).</summary>
    public ObservableCollection<ReleaseItemViewModel> Releases { get; } = [];

    /// <summary>"Loading releases…", why GitHub could not be asked, or that there are none; null when the list says it all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReleasesStatus))]
    public partial string? ReleasesStatus { get; set; }

    public bool HasReleasesStatus => !string.IsNullOrEmpty(ReleasesStatus);

    [RelayCommand]
    private void ToggleAutoUpdate() => AutoUpdate = !AutoUpdate;

    [RelayCommand]
    private void ToggleIncludePrereleases() => IncludePrereleases = !IncludePrereleases;

    partial void OnAutoUpdateChanged(bool value) => Apply(s => s.AutoUpdate = value, appearance: false);

    partial void OnIncludePrereleasesChanged(bool value)
    {
        Apply(s => s.IncludePrereleases = value, appearance: false);
        if (_loaded)
        {
            Updates.OnPrereleasesChanged();
            ShowReleases();
        }
    }

    /// <summary>Reads the release list the first time the Updates page is shown (once per run; the feed caches it).</summary>
    private async Task LoadReleasesAsync()
    {
        if (_releasesRequested)
        {
            return;
        }

        _releasesRequested = true;
        ReleasesStatus = "Loading releases…";
        try
        {
            _releaseList = await Updates.GetReleasesAsync();
        }
        catch (Exception ex)
        {
            ReleasesStatus = "The releases could not be read: " + ex.GetBaseException().Message;
            return;
        }

        ShowReleases();
    }

    private void ShowReleases()
    {
        if (_releaseList is not { } list)
        {
            return;
        }

        Releases.Clear();
        var expanded = 0;
        foreach (var release in list.Releases)
        {
            var installed = Updates.IsCurrent(release.Version);
            if (release.Prerelease && !IncludePrereleases && !installed)
            {
                continue;
            }

            // The newest release and the ones newer than this build (up to five) start expanded.
            var newer = Updates.IsNewer(release.Version);
            var expand = Releases.Count == 0 || (newer && expanded < 5);
            expanded += expand ? 1 : 0;
            Releases.Add(new ReleaseItemViewModel(release, installed, newer) { IsExpanded = expand });
        }

        ReleasesStatus = list.Problem ?? (Releases.Count == 0 ? "No releases yet." : null);
    }

    // ------------------------------------------------------------------ helpers

    private void Apply(Action<KlodiSettings> change, bool appearance)
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
