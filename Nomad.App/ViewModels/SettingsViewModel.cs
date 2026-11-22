using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Infrastructure;
using Nomad.App.Updates;
using Nomad.Core.Process;
using Nomad.Core.Remote;
using Nomad.Core.Settings;
using Nomad.Core.Updates;

namespace Nomad.App.ViewModels;

/// <summary>
/// The settings dialog's pages, in the order of its navigation.
/// </summary>
public enum SettingsPageKind
{
    General,
    Appearance,
    Files,
    Agents,
    RemoteControl,
    Hub,
    Updates,
}

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
    private ReleaseList? _releaseList;
    private bool _releasesRequested;

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
        EditorFontSize = s.EditorFontSize;
        TextSize = s.TextSize;
        InterfaceTextSize = s.InterfaceTextSize;
        TranscriptWidth = s.TranscriptWidth;
        Theme = s.Theme;
        AgentAccounts =
        [
            new(
                Nomad.Core.Agents.AgentBackends.ClaudeCode,
                "Claude Code",
                "Anthropic's agent, on your Claude Pro or Max subscription.",
                "claude",
                "Install it from claude.ai/code",
                s.CliPath,
                path => Apply(st => st.CliPath = path, appearance: false),
                () => _main.OnAgentAuthChanged(Nomad.Core.Agents.AgentBackends.ClaudeCode)),
            new(
                Nomad.Core.Agents.AgentBackends.Codex,
                "Codex",
                "OpenAI's agent, on your ChatGPT Plus or Pro subscription.",
                "codex",
                "Install it with npm i -g @openai/codex",
                s.CodexPath,
                path =>
                {
                    Apply(st => st.CodexPath = path, appearance: false);
                    Nomad.Core.Codex.CodexServer.CliPath = path;
                },
                () => _main.OnAgentAuthChanged(Nomad.Core.Agents.AgentBackends.Codex)),
            new(
                Nomad.Core.Agents.AgentBackends.Antigravity,
                "Antigravity",
                "Google's coding agent, on your Google account.",
                "agy",
                "Install the Antigravity CLI (agy)",
                s.AntigravityPath,
                path =>
                {
                    Apply(st => st.AntigravityPath = path, appearance: false);
                    Nomad.Core.Antigravity.AntigravityBackend.CliPath = path;
                },
                () => _main.OnAgentAuthChanged(Nomad.Core.Agents.AgentBackends.Antigravity)),
            new(
                Nomad.Core.Agents.AgentBackends.OpenCode,
                "OpenCode",
                "Connect subscription providers (ChatGPT, GitHub Copilot, SuperGrok, …) or use free OpenCode Zen.",
                "opencode",
                "Install it with npm i -g opencode-ai",
                s.OpenCodePath,
                path =>
                {
                    Apply(st => st.OpenCodePath = path, appearance: false);
                    Nomad.Core.OpenCode.OpenCodeServer.CliPath = path;
                },
                () => _main.OnAgentAuthChanged(Nomad.Core.Agents.AgentBackends.OpenCode)),
        ];
        MainBackend = MainBackends.FirstOrDefault(b => b.Id == (s.MainBackend ?? "")) ?? MainBackends[0];
        AutoUpdate = s.AutoUpdate;
        AutoArchive = s.AutoArchive;
        SleepIdle = s.SleepIdleSessions;
        SleepAfterHoursText = Math.Clamp(s.SleepAfterHours, 1, NomadSettings.MaxSleepAfterHours).ToString(System.Globalization.CultureInfo.InvariantCulture);
        CloseToTray = s.CloseToTray;
        NotifyWhenDone = s.NotifyWhenDone;
        FlashTaskbar = s.FlashTaskbar;
        ShowUsageRing = s.ShowUsageRing;
        ShowNewSessionUsage = s.ShowNewSessionUsage;
        FilesViewEnabled = s.FilesViewEnabled;
        FileTreeOnRight = s.FileTreeOnRight;
        FilesViewKey = s.FilesViewKey;
        CompactModelPicker = s.CompactModelPicker;
        AutoArchiveHoursText = Math.Max(1, s.AutoArchiveHours).ToString(System.Globalization.CultureInfo.InvariantCulture);
        IncludePrereleases = s.IncludePrereleases;
        DefaultBackend = Backends.FirstOrDefault(b => b.Id == Nomad.Core.Agents.AgentBackends.Normalize(s.DefaultBackend)) ?? Backends[0];
        RefreshDefaultModels();
        DefaultMode = Catalog.Modes.FirstOrDefault(m => m.Id == s.DefaultPermissionMode) ?? Catalog.Modes[1];
        DefaultEffort = Catalog.Efforts.FirstOrDefault(e => e.Id == s.DefaultEffort) ?? Catalog.Efforts[0];
        RemoteControlByDefault = s.RemoteControlByDefault;
        PromptSuggestions = s.PromptSuggestions;
        SuggestNewChats = s.SuggestNewChats;
        foreach (var folder in s.NoNewChatFolders)
        {
            NoNewChatFolders.Add(folder);
        }
        ConfirmModelSwitch = s.ConfirmModelSwitch;
        ModelFunctionKeys = s.ModelFunctionKeys;
        LoadColorThemes(s.ColorTheme);
        RefreshPhoneFolders();
        RefreshFolderSystemPrompts();
        main.PhoneServerChanged += OnPhoneServerChanged;
        _loaded = true;
    }

    public IReadOnlyList<string> UiFonts { get; }

    public IReadOnlyList<string> CodeFonts { get; }

    /// <summary>The code editor's sizes on offer, in points.</summary>
    public IReadOnlyList<int> EditorFontSizes { get; } = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24];

    public IReadOnlyList<Nomad.Core.Agents.AgentBackendInfo> Backends => Nomad.Core.Agents.AgentBackends.All;

    public ObservableCollection<ModelOption> DefaultModelChoices { get; } = [];

    public IReadOnlyList<ModeOption> Modes => Catalog.Modes;

    public IReadOnlyList<EffortOption> Efforts => Catalog.Efforts;

    public string SettingsPath => _main.SettingsPath;

    // ------------------------------------------------------------------ navigation

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralPage), nameof(IsAppearancePage), nameof(IsFilesPage), nameof(IsAgentsPage))]
    [NotifyPropertyChangedFor(nameof(IsRemoteControlPage), nameof(IsHubPage), nameof(IsUpdatesPage))]
    public partial SettingsPageKind Page { get; set; }

    public bool IsGeneralPage => Page == SettingsPageKind.General;

    public bool IsAppearancePage => Page == SettingsPageKind.Appearance;

    public bool IsFilesPage => Page == SettingsPageKind.Files;

    public bool IsAgentsPage => Page == SettingsPageKind.Agents;

    public bool IsRemoteControlPage => Page == SettingsPageKind.RemoteControl;

    public bool IsHubPage => Page == SettingsPageKind.Hub;

    public bool IsUpdatesPage => Page == SettingsPageKind.Updates;

    /// <summary>Nomad Hub (Settings › Hub).</summary>
    public HubViewModel Hub => _main.Hub;

    /// <summary>
    /// Shows a page; the navigation's buttons pass theirs.
    /// </summary>
    [RelayCommand]
    private void ShowPage(SettingsPageKind page) => Page = page;

    /// <summary>
    /// The Agents page checks each CLI's sign-in status, Hub refreshes devices, and Updates loads releases when shown.
    /// </summary>
    partial void OnPageChanged(SettingsPageKind value)
    {
        if (value == SettingsPageKind.Agents) _ = RefreshAgentsAuthAsync();
        if (value == SettingsPageKind.Hub) _ = Hub.RefreshDevicesAsync();
        if (value == SettingsPageKind.Updates) _ = LoadReleasesAsync();
    }

    private Task? _agentsAuthTask;

    /// <summary>Starts or returns the background check of every coding agent's CLI sign-in status.</summary>
    public Task WaitForAgentsAuthAsync() => RefreshAgentsAuthAsync();

    private Task RefreshAgentsAuthAsync()
    {
        return _agentsAuthTask ??= Task.WhenAll(AgentAccounts.Select(a => a.RefreshStatusAsync()));
    }

    // ------------------------------------------------------------------ General › Appearance

    [ObservableProperty]
    public partial string InterfaceFont { get; set; }

    [ObservableProperty]
    public partial string CodeFont { get; set; }

    [ObservableProperty]
    public partial int EditorFontSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextSmall), nameof(IsTextMedium), nameof(IsTextLarge))]
    public partial string TextSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUiTextSmall), nameof(IsUiTextMedium), nameof(IsUiTextLarge))]
    public partial string InterfaceTextSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWidthNarrow), nameof(IsWidthMedium), nameof(IsWidthWide))]
    public partial string TranscriptWidth { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeDark), nameof(IsThemeLight), nameof(IsThemeSystem))]
    public partial string Theme { get; set; }

    public bool IsThemeDark { get => Theme is not ("Light" or "System"); set => SetIf(value, () => Theme = "Dark"); }

    public bool IsThemeLight { get => Theme == "Light"; set => SetIf(value, () => Theme = "Light"); }

    public bool IsThemeSystem { get => Theme == "System"; set => SetIf(value, () => Theme = "System"); }

    partial void OnThemeChanged(string value)
    {
        Apply(s => s.Theme = value, appearance: true);
        RefreshThemeColors();
    }

    public bool IsTextSmall { get => TextSize == "Small"; set => SetIf(value, () => TextSize = "Small"); }

    public bool IsTextMedium { get => TextSize is not ("Small" or "Large"); set => SetIf(value, () => TextSize = "Medium"); }

    public bool IsTextLarge { get => TextSize == "Large"; set => SetIf(value, () => TextSize = "Large"); }

    public bool IsUiTextSmall { get => InterfaceTextSize == "Small"; set => SetIf(value, () => InterfaceTextSize = "Small"); }

    public bool IsUiTextMedium { get => InterfaceTextSize is not ("Small" or "Large"); set => SetIf(value, () => InterfaceTextSize = "Medium"); }

    public bool IsUiTextLarge { get => InterfaceTextSize == "Large"; set => SetIf(value, () => InterfaceTextSize = "Large"); }

    /// <summary>The size Claude's workflows aim for: small | medium | large | unrestricted.</summary>
    public string WorkflowSize => _main.Settings.WorkflowSize ?? "medium";

    public bool IsWorkflowSmall { get => WorkflowSize == "small"; set => SetIf(value, () => SetWorkflowSize("small")); }

    public bool IsWorkflowMedium { get => WorkflowSize == "medium"; set => SetIf(value, () => SetWorkflowSize("medium")); }

    public bool IsWorkflowLarge { get => WorkflowSize == "large"; set => SetIf(value, () => SetWorkflowSize("large")); }

    public bool IsWorkflowUnrestricted { get => WorkflowSize == "unrestricted"; set => SetIf(value, () => SetWorkflowSize("unrestricted")); }

    private void SetWorkflowSize(string size)
    {
        if (size == WorkflowSize)
        {
            return;
        }

        _main.SetWorkflowSize(size);
        OnPropertyChanged(nameof(IsWorkflowSmall));
        OnPropertyChanged(nameof(IsWorkflowMedium));
        OnPropertyChanged(nameof(IsWorkflowLarge));
        OnPropertyChanged(nameof(IsWorkflowUnrestricted));
    }

    public bool IsWidthNarrow { get => TranscriptWidth == "Narrow"; set => SetIf(value, () => TranscriptWidth = "Narrow"); }

    public bool IsWidthMedium { get => TranscriptWidth == "Medium"; set => SetIf(value, () => TranscriptWidth = "Medium"); }

    public bool IsWidthWide { get => TranscriptWidth is not ("Narrow" or "Medium"); set => SetIf(value, () => TranscriptWidth = "Wide"); }

    partial void OnInterfaceFontChanged(string value) => Apply(s => s.InterfaceFont = string.IsNullOrWhiteSpace(value) ? "Inter" : value, appearance: true);

    partial void OnCodeFontChanged(string value) => Apply(s => s.CodeFont = string.IsNullOrWhiteSpace(value) ? "Cascadia Mono" : value, appearance: true);

    partial void OnEditorFontSizeChanged(int value) => Apply(s => s.EditorFontSize = value > 0 ? value : 10, appearance: true);

    partial void OnTextSizeChanged(string value) => Apply(s => s.TextSize = value, appearance: true);

    partial void OnInterfaceTextSizeChanged(string value) => Apply(s => s.InterfaceTextSize = value, appearance: true);

    partial void OnTranscriptWidthChanged(string value) => Apply(s => s.TranscriptWidth = value, appearance: true);

    // ------------------------------------------------------------------ Agents

    /// <summary>All four coding agents with their CLI path, sign-in status, and browser subscription login.</summary>
    public IReadOnlyList<AgentAccountViewModel> AgentAccounts { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultModelHint))]
    public partial Nomad.Core.Agents.AgentBackendInfo DefaultBackend { get; set; }

    [ObservableProperty]
    public partial ModelOption? DefaultModel { get; set; }

    public string DefaultModelHint =>
        $"Starting model for new {DefaultBackend?.Name ?? "agent"} sessions; each session can switch from the status row.";

    [ObservableProperty]
    public partial ModeOption DefaultMode { get; set; }

    [ObservableProperty]
    public partial EffortOption DefaultEffort { get; set; }

    private bool _refreshingModels;

    partial void OnDefaultBackendChanged(Nomad.Core.Agents.AgentBackendInfo value)
    {
        if (value is null)
        {
            return;
        }

        Apply(s => s.DefaultBackend = value.Id, appearance: false);
        RefreshDefaultModels();
        _ = LoadDefaultBackendCatalogIfNeededAsync(value.Id);
    }

    private async Task LoadDefaultBackendCatalogIfNeededAsync(string backendId)
    {
        if (backendId == Nomad.Core.Agents.AgentBackends.Codex && CodexCatalog.Models.Count == 0)
        {
            await _main.LoadCodexCatalogAsync();
            if (DefaultBackend?.Id == backendId) RefreshDefaultModels();
        }
        else if (backendId == Nomad.Core.Agents.AgentBackends.OpenCode && OpenCodeCatalog.Models.Count == 0)
        {
            await _main.LoadOpenCodeCatalogAsync();
            if (DefaultBackend?.Id == backendId) RefreshDefaultModels();
        }
    }

    private void RefreshDefaultModels()
    {
        _refreshingModels = true;
        try
        {
            DefaultModelChoices.Clear();
            var backend = DefaultBackend?.Id ?? Nomad.Core.Agents.AgentBackends.ClaudeCode;
            var s = _main.Settings;

            if (backend == Nomad.Core.Agents.AgentBackends.Antigravity)
            {
                var models = Catalog.AvailableAntigravityModels(s.AntigravityModels);
                foreach (var m in models) DefaultModelChoices.Add(m);
                var current = s.AntigravityModel ?? Nomad.Core.Antigravity.AntigravityBackend.DefaultModel;
                DefaultModel = DefaultModelChoices.FirstOrDefault(m => m.Id == current) ?? DefaultModelChoices.FirstOrDefault();
            }
            else if (backend == Nomad.Core.Agents.AgentBackends.Codex)
            {
                DefaultModelChoices.Add(new("", "Codex default", 0));
                foreach (var m in CodexCatalog.Models)
                {
                    DefaultModelChoices.Add(new(m.Id, m.Name, 0));
                }

                var current = s.CodexModel ?? "";
                if (current.Length > 0 && DefaultModelChoices.All(m => m.Id != current))
                {
                    DefaultModelChoices.Add(new(current, Catalog.ModelName(current), 0));
                }

                DefaultModel = DefaultModelChoices.FirstOrDefault(m => m.Id == current) ?? DefaultModelChoices[0];
            }
            else if (backend == Nomad.Core.Agents.AgentBackends.OpenCode)
            {
                DefaultModelChoices.Add(new("", "OpenCode default", 0));
                foreach (var m in OpenCodeCatalog.Models)
                {
                    DefaultModelChoices.Add(new(m.Id, $"{m.Name} ({m.ProviderName})", 0));
                }

                var current = s.OpenCodeModel ?? "";
                if (current.Length > 0 && DefaultModelChoices.All(m => m.Id != current))
                {
                    DefaultModelChoices.Add(new(current, Catalog.ModelName(current), 0));
                }

                DefaultModel = DefaultModelChoices.FirstOrDefault(m => m.Id == current) ?? DefaultModelChoices[0];
            }
            else
            {
                foreach (var m in Catalog.Models) DefaultModelChoices.Add(m);
                DefaultModel = DefaultModelChoices.FirstOrDefault(m => m.Id == s.DefaultModel) ?? DefaultModelChoices.FirstOrDefault();
            }
        }
        finally
        {
            _refreshingModels = false;
        }
    }

    /// <summary>"None" (every row shows its agent's logo), then each agent.</summary>
    public IReadOnlyList<Nomad.Core.Agents.AgentBackendInfo> MainBackends { get; } =
        [new("", "None", ""), .. Nomad.Core.Agents.AgentBackends.All];

    [ObservableProperty]
    public partial Nomad.Core.Agents.AgentBackendInfo MainBackend { get; set; }

    partial void OnMainBackendChanged(Nomad.Core.Agents.AgentBackendInfo value)
    {
        var id = string.IsNullOrEmpty(value?.Id) ? null : value.Id;
        Apply(s => s.MainBackend = id, appearance: false);
        _main.ApplyMainBackend();
    }

    partial void OnDefaultModelChanged(ModelOption? value)
    {
        if (_refreshingModels || value is null)
        {
            return;
        }

        var backend = DefaultBackend?.Id ?? Nomad.Core.Agents.AgentBackends.ClaudeCode;
        Apply(s =>
        {
            switch (backend)
            {
                case Nomad.Core.Agents.AgentBackends.Antigravity:
                    s.AntigravityModel = value.Id;
                    break;
                case Nomad.Core.Agents.AgentBackends.Codex:
                    s.CodexModel = string.IsNullOrEmpty(value.Id) ? null : value.Id;
                    break;
                case Nomad.Core.Agents.AgentBackends.OpenCode:
                    s.OpenCodeModel = string.IsNullOrEmpty(value.Id) ? null : value.Id;
                    break;
                default:
                    s.DefaultModel = value.Id;
                    break;
            }
        }, appearance: false);
    }

    partial void OnDefaultModeChanged(ModeOption value)
    {
        Apply(s => s.DefaultPermissionMode = value.Id, appearance: false);
        if (_loaded)
        {
            _main.OnDefaultModeChanged();
        }
    }

    partial void OnDefaultEffortChanged(EffortOption value) => Apply(
        s =>
        {
            // Choosing a new default effort clears the current default model's saved override so it takes effect right away.
            s.DefaultEffort = value.Id;
            if (DefaultModel is { Id.Length: > 0 } current)
            {
                foreach (var key in s.EffortByModel.Keys.Where(k => string.Equals(k, current.Id, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    s.EffortByModel.Remove(key);
                }
            }
        },
        appearance: false);

    // ------------------------------------------------------------------ Claude Code › Remote Control

    [ObservableProperty]
    public partial bool RemoteControlByDefault { get; set; }

    [RelayCommand]
    private void ToggleRemoteControlByDefault() => RemoteControlByDefault = !RemoteControlByDefault;

    partial void OnRemoteControlByDefaultChanged(bool value) => Apply(s => s.RemoteControlByDefault = value, appearance: false);

    [ObservableProperty]
    public partial bool PromptSuggestions { get; set; }

    [RelayCommand]
    private void TogglePromptSuggestions() => PromptSuggestions = !PromptSuggestions;

    partial void OnPromptSuggestionsChanged(bool value) => Apply(s => s.PromptSuggestions = value, appearance: false);

    /// <summary>Claude Code may suggest a task for a chat of its own (Settings → Agents).</summary>
    [ObservableProperty]
    public partial bool SuggestNewChats { get; set; } = true;

    [RelayCommand]
    private void ToggleSuggestNewChats() => SuggestNewChats = !SuggestNewChats;

    partial void OnSuggestNewChatsChanged(bool value) => Apply(s => s.SuggestNewChats = value, appearance: false);

    /// <summary>Folders where Claude Code never suggests a new chat (and the folders inside them).</summary>
    public ObservableCollection<string> NoNewChatFolders { get; } = [];

    public bool HasNoNewChatFolders => NoNewChatFolders.Count > 0;

    public void AddNoNewChatFolder(string folder)
    {
        if (NoNewChatFolders.Any(f => string.Equals(f.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        NoNewChatFolders.Add(folder);
        SaveNoNewChatFolders();
    }

    [RelayCommand]
    private void RemoveNoNewChatFolder(string folder)
    {
        NoNewChatFolders.Remove(folder);
        SaveNoNewChatFolders();
    }

    private void SaveNoNewChatFolders()
    {
        OnPropertyChanged(nameof(HasNoNewChatFolders));
        Apply(s => s.NoNewChatFolders = [.. NoNewChatFolders], appearance: false);
    }

    [ObservableProperty]
    public partial bool ConfirmModelSwitch { get; set; }

    [RelayCommand]
    private void ToggleConfirmModelSwitch() => ConfirmModelSwitch = !ConfirmModelSwitch;

    partial void OnConfirmModelSwitchChanged(bool value) => Apply(s => s.ConfirmModelSwitch = value, appearance: false);

    [ObservableProperty]
    public partial bool ModelFunctionKeys { get; set; }

    [RelayCommand]
    private void ToggleModelFunctionKeys() => ModelFunctionKeys = !ModelFunctionKeys;

    partial void OnModelFunctionKeysChanged(bool value) => Apply(s => s.ModelFunctionKeys = value, appearance: false);

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [RelayCommand]
    private void ToggleCloseToTray() => CloseToTray = !CloseToTray;

    partial void OnCloseToTrayChanged(bool value)
    {
        Apply(s => s.CloseToTray = value, appearance: false);
        if (_loaded)
        {
            _main.OnCloseToTrayChanged();
        }
    }

    [ObservableProperty]
    public partial bool NotifyWhenDone { get; set; }

    [RelayCommand]
    private void ToggleNotifyWhenDone() => NotifyWhenDone = !NotifyWhenDone;

    partial void OnNotifyWhenDoneChanged(bool value) => Apply(s => s.NotifyWhenDone = value, appearance: false);

    [ObservableProperty]
    public partial bool FlashTaskbar { get; set; }

    [RelayCommand]
    private void ToggleFlashTaskbar() => FlashTaskbar = !FlashTaskbar;

    partial void OnFlashTaskbarChanged(bool value) => Apply(s => s.FlashTaskbar = value, appearance: false);

    [ObservableProperty]
    public partial bool ShowUsageRing { get; set; }

    [ObservableProperty]
    public partial bool ShowNewSessionUsage { get; set; }

    [RelayCommand]
    private void ToggleShowNewSessionUsage() => ShowNewSessionUsage = !ShowNewSessionUsage;

    partial void OnShowNewSessionUsageChanged(bool value)
    {
        if (_loaded)
        {
            _main.ShowNewSessionUsage = value;
        }
    }

    [RelayCommand]
    private void ToggleShowUsageRing() => ShowUsageRing = !ShowUsageRing;

    partial void OnShowUsageRingChanged(bool value)
    {
        if (_loaded)
        {
            _main.ShowUsageRing = value;
        }
    }

    [ObservableProperty]
    public partial bool FileTreeOnRight { get; set; }

    /// <summary>Chats have a Files view (folder tree and code editor).</summary>
    [ObservableProperty]
    public partial bool FilesViewEnabled { get; set; } = true;

    [RelayCommand]
    private void ToggleFilesViewEnabled() => FilesViewEnabled = !FilesViewEnabled;

    partial void OnFilesViewEnabledChanged(bool value)
    {
        if (_loaded)
        {
            _main.FilesViewEnabled = value;
        }
    }

    [RelayCommand]
    private void ToggleFileTreeOnRight() => FileTreeOnRight = !FileTreeOnRight;

    partial void OnFileTreeOnRightChanged(bool value)
    {
        if (_loaded)
        {
            _main.FileTreeOnRight = value;
        }
    }

    /// <summary>The key that switches a chat between Chat and Files, as shown on its button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesViewKeyText))]
    [NotifyPropertyChangedFor(nameof(IsFilesViewKeyChanged))]
    public partial string FilesViewKey { get; set; } = "Tab";

    /// <summary>Waiting for the key to bind (the button was clicked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesViewKeyText))]
    public partial bool IsCapturingFilesKey { get; set; }

    public string FilesViewKeyText => IsCapturingFilesKey ? "Press a key…" : string.IsNullOrEmpty(FilesViewKey) ? "None" : FilesViewKey;

    public bool IsFilesViewKeyChanged => FilesViewKey != "Tab";

    [RelayCommand]
    private void CaptureFilesKey() => IsCapturingFilesKey = !IsCapturingFilesKey;

    [RelayCommand]
    private void ResetFilesKey() => SetFilesViewKey("Tab");

    public void SetFilesViewKey(string key)
    {
        IsCapturingFilesKey = false;
        FilesViewKey = key;
        _main.FilesViewKey = key;
    }

    [ObservableProperty]
    public partial bool CompactModelPicker { get; set; }

    [RelayCommand]
    private void ToggleCompactModelPicker() => CompactModelPicker = !CompactModelPicker;

    partial void OnCompactModelPickerChanged(bool value)
    {
        if (_loaded)
        {
            _main.CompactModelPicker = value;
        }
    }

    /// <summary>Folders the phone can start sessions in, each with its server's state.</summary>
    public ObservableCollection<PhoneFolderRow> PhoneFolders { get; } = [];

    public bool HasPhoneFolders => PhoneFolders.Count > 0;

    public void AddPhoneFolder(string folder)
    {
        _main.AddPhoneFolder(folder);
        RefreshPhoneFolders();
    }

    [RelayCommand]
    private async Task RemovePhoneFolderAsync(PhoneFolderRow row)
    {
        await _main.RemovePhoneFolderAsync(row.Folder);
        RefreshPhoneFolders();
    }

    [RelayCommand]
    private void RetryPhoneFolder(PhoneFolderRow row) => _main.RetryPhoneServer(row.Folder);

    /// <summary>
    /// The CLI's one-time questions (turning Remote Control on, trusting the folder) need a terminal:
    /// open one in the folder running <c>claude remote-control</c>; the user answers, then presses Ctrl+C.
    /// </summary>
    [RelayCommand]
    private void SetUpPhoneFolder(PhoneFolderRow row)
    {
        var cli = CliLocator.Resolve(_main.Settings.CliPath) ?? "claude";
        if (Infrastructure.PlatformShell.OpenTerminalCommand(row.Folder, cli, "remote-control"))
        {
            row.Status = "Answer Claude Code in the terminal, press Ctrl+C there, then Retry.";
            row.CanRetry = true;
        }
        else
        {
            row.Status = "Could not open a terminal.";
        }
    }

    /// <summary>Folders whose sessions get a system prompt file in place of the agent's own.</summary>
    public ObservableCollection<FolderSystemPromptRow> FolderSystemPrompts { get; } = [];

    public bool HasFolderSystemPrompts => FolderSystemPrompts.Count > 0;

    /// <summary>
    /// Sets (or clears, null) a folder's system prompt file for Claude Code, Codex or both, and refreshes the list.
    /// </summary>
    public void SetFolderSystemPrompt(string folder, string? file, bool claude = true, bool codex = true)
    {
        _main.SetFolderSystemPrompt(folder, file, claude, codex);
        RefreshFolderSystemPrompts();
    }

    [RelayCommand]
    private void RemoveFolderSystemPrompt(FolderSystemPromptRow row)
    {
        _main.RemoveFolderSystemPrompts(row.Folder);
        RefreshFolderSystemPrompts();
    }

    /// <summary>
    /// Gives Claude Code its own prompt in a listed folder.
    /// </summary>
    [RelayCommand]
    private void ClearClaudeSystemPrompt(FolderSystemPromptRow row) => SetFolderSystemPrompt(row.Folder, null, codex: false);

    /// <summary>
    /// Gives Codex its own prompt in a listed folder.
    /// </summary>
    [RelayCommand]
    private void ClearCodexSystemPrompt(FolderSystemPromptRow row) => SetFolderSystemPrompt(row.Folder, null, claude: false);

    private void RefreshFolderSystemPrompts()
    {
        FolderSystemPrompts.Clear();

        var claude = _main.Settings.FolderSystemPrompts;
        var codex = _main.Settings.FolderCodexSystemPrompts ?? claude;
        var folders = claude.Keys
            .Concat(codex.Keys)
            .Select(f => f.TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            FolderSystemPrompts.Add(new FolderSystemPromptRow(folder, FolderPrompt(claude, folder), FolderPrompt(codex, folder)));
        }

        OnPropertyChanged(nameof(HasFolderSystemPrompts));
    }

    /// <summary>
    /// One agent's entry for a folder, however its path ends; null when it has none.
    /// </summary>
    private static string? FolderPrompt(Dictionary<string, string> prompts, string folder) => prompts
        .FirstOrDefault(p => string.Equals(p.Key.TrimEnd('\\', '/'), folder, StringComparison.OrdinalIgnoreCase))
        .Value;

    private void RefreshPhoneFolders()
    {
        PhoneFolders.Clear();
        foreach (var folder in _main.Settings.PhoneFolders)
        {
            var row = new PhoneFolderRow(folder);
            row.Update(_main.PhoneServer(folder));
            PhoneFolders.Add(row);
        }

        OnPropertyChanged(nameof(HasPhoneFolders));
    }

    private void OnPhoneServerChanged(string folder)
    {
        foreach (var row in PhoneFolders.Where(r => string.Equals(r.Folder.TrimEnd('\\', '/'), folder, StringComparison.OrdinalIgnoreCase)))
        {
            row.Update(_main.PhoneServer(folder));
        }
    }

    /// <summary>Stops listening to the app (the dialog closed).</summary>
    public void Detach()
    {
        _main.PhoneServerChanged -= OnPhoneServerChanged;
        foreach (var agent in AgentAccounts)
        {
            agent.Detach();
        }

        FlushThemeSave();
    }

    // ------------------------------------------------------------------ Sessions

    [ObservableProperty]
    public partial bool AutoArchive { get; set; }

    /// <summary>The auto-archive time in whole hours, as typed: a number below 1 becomes 1, anything else waits.</summary>
    [ObservableProperty]
    public partial string AutoArchiveHoursText { get; set; } = "24";

    [RelayCommand]
    private void ToggleAutoArchive() => AutoArchive = !AutoArchive;

    /// <summary>Whether a quiet session's process is ended after <see cref="SleepAfterHoursText"/> hours.</summary>
    [ObservableProperty]
    public partial bool SleepIdle { get; set; } = true;

    /// <summary>
    /// The idle time in whole hours, as typed: below 1 becomes 1, above a year a year, and it is saved once it has settled.
    /// </summary>
    [ObservableProperty]
    public partial string SleepAfterHoursText { get; set; } = "1";

    [RelayCommand]
    private void ToggleSleepIdle() => SleepIdle = !SleepIdle;

    partial void OnSleepIdleChanged(bool value) => Apply(s => s.SleepIdleSessions = value, appearance: false);

    partial void OnSleepAfterHoursTextChanged(string value)
    {
        if (!int.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hours))
        {
            return;
        }

        if (hours < 1 || hours > NomadSettings.MaxSleepAfterHours)
        {
            SleepAfterHoursText = Math.Clamp(hours, 1, NomadSettings.MaxSleepAfterHours).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }

        if (!_loaded)
        {
            Apply(s => s.SleepAfterHours = hours, appearance: false);
            return;
        }

        // Typing "12" passes through "1": saved only once the number has settled, so no session is ended on the way.
        _sleepDelay ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
        {
            _sleepDelay!.Stop();
            if (int.TryParse(SleepAfterHoursText.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var settled)
                && settled >= 1 && settled <= NomadSettings.MaxSleepAfterHours)
            {
                Apply(s => s.SleepAfterHours = settled, appearance: false);
            }
        });
        _sleepDelay.Stop();
        _sleepDelay.Start();
    }

    private Avalonia.Threading.DispatcherTimer? _sleepDelay;

    partial void OnAutoArchiveChanged(bool value)
    {
        Apply(s => s.AutoArchive = value, appearance: false);
        if (_loaded)
        {
            _main.ApplyAutoArchive();
        }
    }

    partial void OnAutoArchiveHoursTextChanged(string value)
    {
        if (!int.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hours))
        {
            return;
        }

        if (hours < 1)
        {
            AutoArchiveHoursText = "1";
            return;
        }

        Apply(s => s.AutoArchiveHours = hours, appearance: false);
        if (_loaded)
        {
            // Typing "12" passes through "1": archive only once the number has settled.
            _archiveDelay ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
            {
                _archiveDelay!.Stop();
                _main.ApplyAutoArchive();
            });
            _archiveDelay.Stop();
            _archiveDelay.Start();
        }
    }

    private Avalonia.Threading.DispatcherTimer? _archiveDelay;

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

    // ------------------------------------------------------------------ General › Color theme

    // The custom theme selected (and edited); null while it is the built-in one.
    private CustomTheme? _theme;

    public ObservableCollection<ThemeChoice> ColorThemes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomTheme))]
    [NotifyCanExecuteChangedFor(nameof(DeleteThemeCommand))]
    public partial ThemeChoice? SelectedColorTheme { get; set; }

    public bool IsCustomTheme => SelectedColorTheme is { IsBuiltIn: false };

    [ObservableProperty]
    public partial string ThemeName { get; set; } = "";

    /// <summary>The selected custom theme's colors in the mode on screen, by group.</summary>
    public ObservableCollection<ThemeColorGroup> ThemeColorGroups { get; } = [];

    [ObservableProperty]
    public partial string ThemeColorsHint { get; set; } = "";

    partial void OnSelectedColorThemeChanged(ThemeChoice? value)
    {
        if (value is null)
        {
            return;
        }

        FlushThemeSave();
        _theme = value.IsBuiltIn ? null : ColorThemeStore.Load(Themes.Folder, value.Id) ?? new CustomTheme { Name = value.Name };
        if (_theme is not null)
        {
            value.Colors = _theme;
        }

        ThemeName = _theme?.Name ?? "";
        Apply(s => s.ColorTheme = value.Id, appearance: true);
        RefreshThemeColors();
    }

    partial void OnThemeNameChanged(string value)
    {
        var name = value.Trim();
        if (!_loaded || _theme is null || SelectedColorTheme is not { } choice || name.Length == 0 || name == _theme.Name)
        {
            return;
        }

        _theme.Name = name;
        choice.Name = name;
        SaveTheme();
    }

    /// <summary>A new theme with the selected one's colors (none of its own for Nomad), selected.</summary>
    [RelayCommand]
    private void DuplicateTheme()
    {
        if (SelectedColorTheme is not { } source)
        {
            return;
        }

        AddTheme(new CustomTheme
        {
            Name = UniqueThemeName(source.Name + " copy"),
            Dark = new Dictionary<string, string>(source.Colors?.Dark ?? []),
            Light = new Dictionary<string, string>(source.Colors?.Light ?? []),
        });
    }

    /// <summary>Why the last import or export failed; null when it didn't.</summary>
    [ObservableProperty]
    public partial string? ThemeFileProblem { get; set; }

    /// <summary>
    /// The selected theme as JSON to export, with a file name for it. A built-in theme is written with all its base
    /// colors (a starting point for a theme by hand); a custom one with the colors it sets.
    /// </summary>
    public (string FileName, string Json) ExportSelectedTheme()
    {
        var choice = SelectedColorTheme ?? ColorThemes[0];
        Dictionary<string, string> All(ThemeVariant variant, Dictionary<string, string>? own)
        {
            var colors = Themes.DefaultColors(variant);
            foreach (var (key, value) in own ?? [])
            {
                colors[key] = value;
            }

            return colors;
        }

        var theme = _theme ?? new CustomTheme
        {
            Name = choice.Name,
            Dark = All(ThemeVariant.Dark, choice.Colors?.Dark),
            Light = All(ThemeVariant.Light, choice.Colors?.Light),
        };
        var id = choice.Id;
        return (id + ".json", ColorThemeStore.ToJson(theme));
    }

    /// <summary>Adds the theme in an exported file (named after it if it has no name) and selects it.</summary>
    public void ImportTheme(string json, string fileName)
    {
        var theme = ColorThemeStore.FromJson(json);
        if (theme is null)
        {
            ThemeFileProblem = $"{fileName} isn't a theme: it isn't JSON with a name and dark and light colors.";
            return;
        }

        // Only the tokens this version knows, with colors it can read.
        Dictionary<string, string> Valid(Dictionary<string, string> colors) => colors
            .Where(c => Themes.Tokens.Any(t => t.Key == c.Key) && Themes.TryParse(c.Value, out _))
            .ToDictionary(c => c.Key, c => c.Value.Trim().StartsWith('#') ? c.Value.Trim() : "#" + c.Value.Trim());
        var dark = Valid(theme.Dark);
        var light = Valid(theme.Light);
        var name = string.IsNullOrWhiteSpace(theme.Name) ? Path.GetFileNameWithoutExtension(fileName) : theme.Name.Trim();
        if (string.IsNullOrWhiteSpace(theme.Name) && dark.Count == 0 && light.Count == 0)
        {
            ThemeFileProblem = $"{fileName} has no theme in it: no name and no colors Nomad.ai knows.";
            return;
        }

        ThemeFileProblem = null;
        AddTheme(new CustomTheme { Name = UniqueThemeName(name), Dark = dark, Light = light });
    }

    private void AddTheme(CustomTheme theme)
    {
        var id = ColorThemeStore.NewId(Themes.Folder, theme.Name, [Themes.DefaultId, .. ThemePresets.All.Select(p => p.Id)]);
        try
        {
            ColorThemeStore.Save(Themes.Folder, id, theme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ThemeFileProblem = "Couldn't save the theme: " + ex.Message;
            return;
        }

        ThemeFileProblem = null;
        var choice = new ThemeChoice(id, theme.Name, theme);
        choice.Paint(CurrentVariant);
        ColorThemes.Add(choice);
        SelectedColorTheme = choice;
    }

    // "Ocean", or "Ocean 2" (3, …) when a theme is called that already.
    private string UniqueThemeName(string name)
    {
        var unique = name;
        for (var n = 2; ColorThemes.Any(c => string.Equals(c.Name, unique, StringComparison.CurrentCultureIgnoreCase)); n++)
        {
            unique = $"{name} {n}";
        }

        return unique;
    }

    /// <summary>Moves the selected custom theme's file to the Recycle Bin and goes back to the built-in theme.</summary>
    [RelayCommand(CanExecute = nameof(IsCustomTheme))]
    private void DeleteTheme()
    {
        if (SelectedColorTheme is not { IsBuiltIn: false } choice)
        {
            return;
        }

        // A color change not yet written would bring the file back.
        _saveDelay?.Stop();
        _pendingSave = null;

        var path = ColorThemeStore.PathOf(Themes.Folder, choice.Id);
        if (!RecycleBin.Delete([path]))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        SelectedColorTheme = ColorThemes[0];
        ColorThemes.Remove(choice);
    }

    private void LoadColorThemes(string selectedId)
    {
        ColorThemes.Clear();
        ColorThemes.Add(new ThemeChoice(Themes.DefaultId, Themes.DefaultName));
        foreach (var (id, theme) in ThemePresets.All)
        {
            ColorThemes.Add(new ThemeChoice(id, theme.Name, theme));
        }

        foreach (var (id, theme) in ColorThemeStore.LoadAll(Themes.Folder))
        {
            if (!Themes.IsBuiltIn(id))
            {
                ColorThemes.Add(new ThemeChoice(id, theme.Name, theme));
            }
        }

        PaintThemeChoices();

        SelectedColorTheme = ColorThemes.FirstOrDefault(c => c.Id == selectedId) ?? ColorThemes[0];
    }

    private static ThemeVariant CurrentVariant =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Light ? ThemeVariant.Light : ThemeVariant.Dark;

    private void PaintThemeChoices()
    {
        foreach (var choice in ColorThemes)
        {
            choice.Paint(CurrentVariant);
        }
    }

    private void RefreshThemeColors()
    {
        PaintThemeChoices();
        ThemeColorGroups.Clear();
        if (_theme is null)
        {
            return;
        }

        var light = Application.Current?.ActualThemeVariant == ThemeVariant.Light;
        var variant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var colors = light ? _theme.Light : _theme.Dark;
        ThemeColorsHint = light
            ? "These are the light mode's colors; set Theme to Dark for the dark ones."
            : "These are the dark mode's colors; set Theme to Light for the light ones.";
        var allRows = new List<ThemeColorRow>();
        foreach (var group in Themes.Tokens.GroupBy(t => t.Group))
        {
            var rows = group.Select(t => new ThemeColorRow(t, Themes.DefaultColor(t, variant, colors), colors.GetValueOrDefault(t.Key), row =>
            {
                if (row.Override is null)
                {
                    colors.Remove(row.Key);
                }
                else
                {
                    colors[row.Key] = row.Override;
                }

                foreach (var dependent in allRows.Where(r => r.Fallback == row.Key))
                {
                    dependent.UpdateDefault(row.Color);
                }

                ThemeColorChanged();
            })).ToList();
            allRows.AddRange(rows);
            ThemeColorGroups.Add(new ThemeColorGroup(group.Key, rows));
        }
    }

    // A drag in the color picker changes a color many times a second: the app is recolored at most once a frame, and
    // the theme's file written once the color settles.
    private void ThemeColorChanged()
    {
        SelectedColorTheme?.Paint(CurrentVariant);
        if (!_restyleQueued)
        {
            _restyleQueued = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _restyleQueued = false;
                if (_theme is not null && SelectedColorTheme is { IsBuiltIn: false } choice)
                {
                    Themes.Apply(choice.Id, _theme);
                    App.RestyleMarkdown();
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        if (_theme is null || SelectedColorTheme is not { IsBuiltIn: false } current)
        {
            return;
        }

        _pendingSave = (current.Id, _theme);
        _saveDelay ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(400), Avalonia.Threading.DispatcherPriority.Background, (_, _) => FlushThemeSave());
        _saveDelay.Stop();
        _saveDelay.Start();
    }

    private void FlushThemeSave()
    {
        _saveDelay?.Stop();
        if (_pendingSave is not var (id, theme))
        {
            return;
        }

        _pendingSave = null;
        try
        {
            ColorThemeStore.Save(Themes.Folder, id, theme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the next change tries again.
        }
    }

    private bool _restyleQueued;
    private (string Id, CustomTheme Theme)? _pendingSave;
    private Avalonia.Threading.DispatcherTimer? _saveDelay;

    private void SaveTheme()
    {
        if (_theme is null || SelectedColorTheme is not { IsBuiltIn: false } choice)
        {
            return;
        }

        try
        {
            ColorThemeStore.Save(Themes.Folder, choice.Id, _theme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the next change tries again.
        }
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

/// <summary>
/// A folder's system prompts on the settings page: the folder, and the files that replace Claude Code's and Codex's.
/// </summary>
public sealed class FolderSystemPromptRow(string folder, string? claudeFile, string? codexFile)
{
    /// <summary>
    /// The folder the prompts apply in, with the folders inside it.
    /// </summary>
    public string Folder { get; } = folder;

    /// <summary>
    /// Claude Code's file here, or null for its own prompt.
    /// </summary>
    public string? ClaudeFile { get; } = string.IsNullOrEmpty(claudeFile) ? null : claudeFile;

    /// <summary>
    /// Codex's file here, or null for its own prompt.
    /// </summary>
    public string? CodexFile { get; } = string.IsNullOrEmpty(codexFile) ? null : codexFile;

    /// <summary>
    /// Whether Claude Code has a file here (shows its Clear button).
    /// </summary>
    public bool HasClaudeFile => ClaudeFile is not null;

    /// <summary>
    /// Whether Codex has a file here (shows its Clear button).
    /// </summary>
    public bool HasCodexFile => CodexFile is not null;

    /// <summary>
    /// Claude Code's line: its file, or that it uses its own prompt.
    /// </summary>
    public string ClaudeStatus => Status(ClaudeFile);

    /// <summary>
    /// Codex's line: its file, or that it uses its own prompt.
    /// </summary>
    public string CodexStatus => Status(CodexFile);

    /// <summary>
    /// The line for one agent's file: the file, its own prompt, or a missing file (which also means its own prompt).
    /// </summary>
    private static string Status(string? file) => file is null
        ? "Its own prompt"
        : System.IO.File.Exists(file)
            ? file
            : file + " (not there: its own prompt)";
}

/// <summary>A phone folder on the settings page: where the phone can start sessions, and how that is going.</summary>
public sealed partial class PhoneFolderRow(string folder) : ObservableObject
{
    public string Folder { get; } = folder;

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool IsReady { get; set; }

    [ObservableProperty]
    public partial bool NeedsSetup { get; set; }

    [ObservableProperty]
    public partial bool CanRetry { get; set; }

    public void Update(RemoteControlServer? server)
    {
        var state = server?.State ?? RemoteControlServerState.Stopped;
        IsReady = state == RemoteControlServerState.Running;
        NeedsSetup = state == RemoteControlServerState.NeedsSetup;
        CanRetry = state is RemoteControlServerState.NeedsSetup or RemoteControlServerState.Failed or RemoteControlServerState.Stopped;
        Status = state switch
        {
            RemoteControlServerState.Running => "Ready. Start a session here from the Claude app or claude.ai/code.",
            RemoteControlServerState.Starting => "Starting…",
            RemoteControlServerState.NeedsSetup => (server?.Message ?? "Claude Code needs a one-time answer.") + " Set it up in a terminal once.",
            RemoteControlServerState.Failed => server?.Message ?? "Not running.",
            _ => "Not running.",
        };
    }
}

/// <summary>A color theme in the settings' picker, with dots of its background, accent and focus colors.</summary>
public sealed partial class ThemeChoice(string id, string name, CustomTheme? colors = null) : ObservableObject
{
    public string Id { get; } = id;

    public bool IsBuiltIn => Themes.IsBuiltIn(Id);

    [ObservableProperty]
    public partial string Name { get; set; } = name;

    /// <summary>The colors it sets over Nomad's (none for Nomad).</summary>
    public CustomTheme? Colors { get; set; } = colors;

    public SolidColorBrush Background { get; } = new();

    public SolidColorBrush Accent { get; } = new();

    public SolidColorBrush Focus { get; } = new();

    /// <summary>Colors the dots as the theme looks in <paramref name="variant"/>.</summary>
    public void Paint(ThemeVariant variant)
    {
        var own = variant == ThemeVariant.Light ? Colors?.Light : Colors?.Dark;
        Color Of(string token) => Themes.TryParse(own?.GetValueOrDefault(token), out var c) ? c : Themes.DefaultColor(token, variant);
        Background.Color = Of("BgMain");
        Accent.Color = Of("Accent");
        Focus.Color = Of("Ring");
    }
}

/// <summary>A group of a theme's colors in the editor ("Surfaces", "Text").</summary>
public sealed record ThemeColorGroup(string Title, IReadOnlyList<ThemeColorRow> Colors);

/// <summary>One color of a custom theme in the editor: a swatch that opens a picker, and its hex value, which can be typed.</summary>
public sealed partial class ThemeColorRow : ObservableObject
{
    private Color _default;
    private readonly Action<ThemeColorRow> _changed;
    private bool _quiet;

    public ThemeColorRow(ThemeToken token, Color @default, string? value, Action<ThemeColorRow> changed)
    {
        Key = token.Key;
        Label = token.Label;
        Fallback = token.Fallback;
        ResetToolTip = token.Fallback is null ? "Back to Nomad's color" : "Same as Accent";
        _default = @default;
        _changed = changed;
        var own = Themes.TryParse(value, out var parsed);
        var color = own ? parsed : @default;
        Override = own ? Themes.Hex(color) : null;
        Swatch = new SolidColorBrush(color);
        _quiet = true;
        Hex = Themes.Hex(color);
        Color = color;
        _quiet = false;
        IsChanged = own && color != @default;
    }

    public string Key { get; }

    public string Label { get; }

    public string? Fallback { get; }

    public string ResetToolTip { get; }

    /// <summary>The theme's own value; null: the built-in color.</summary>
    public string? Override { get; private set; }

    public SolidColorBrush Swatch { get; }

    [ObservableProperty]
    public partial string Hex { get; set; }

    /// <summary>The color, as the picker sets it.</summary>
    [ObservableProperty]
    public partial Color Color { get; set; }

    /// <summary>It differs from the built-in theme (and can be reset).</summary>
    [ObservableProperty]
    public partial bool IsChanged { get; set; }

    public void UpdateDefault(Color @default)
    {
        _default = @default;
        if (Override is null)
        {
            Swatch.Color = @default;
            _quiet = true;
            Hex = Themes.Hex(@default);
            Color = @default;
            _quiet = false;
            IsChanged = false;
        }
        else
        {
            IsChanged = Color != @default;
        }
    }

    partial void OnHexChanged(string value)
    {
        if (_quiet || !Themes.TryParse(value, out var color))
        {
            return;
        }

        Swatch.Color = color;
        IsChanged = color != _default;
        Override = IsChanged ? Themes.Hex(color) : null;
        _quiet = true;
        Color = color;
        _quiet = false;
        _changed(this);
    }

    partial void OnColorChanged(Color value)
    {
        if (!_quiet)
        {
            Hex = Themes.Hex(value);
        }
    }

    [RelayCommand]
    private void Reset()
    {
        Override = null;
        Swatch.Color = _default;
        _quiet = true;
        Hex = Themes.Hex(_default);
        Color = _default;
        _quiet = false;
        IsChanged = false;
        _changed(this);
    }
}
