using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nomad.Core.Settings;

/// <summary>User preferences, persisted as JSON in <c>%APPDATA%\Nomad\settings.json</c>.</summary>
public sealed class NomadSettings
{
    // General › Appearance
    public string InterfaceFont { get; set; } = "Inter";
    public string CodeFont { get; set; } = "Cascadia Mono";

    /// <summary>Small | Medium | Large (transcript body 13 / 14 / 15 px).</summary>
    public string TextSize { get; set; } = "Medium";

    /// <summary>Small | Medium | Large (sidebar, menus, status row: 12 / 13 / 14 px).</summary>
    public string InterfaceTextSize { get; set; } = "Medium";

    /// <summary>Dark | Light | System (follows the Windows app mode).</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Narrow | Medium | Wide (760 / 1000 / 1280 px column; Wide matches the original).</summary>
    public string TranscriptWidth { get; set; } = "Wide";

    /// <summary>Show the activity lines (thinking, tool groups such as "Read a file"); off: only prompts and replies.</summary>
    public bool ShowActivity { get; set; } = true;

    /// <summary>After a turn, the CLI guesses the next prompt; it shows as ghost text in an empty composer.</summary>
    public bool PromptSuggestions { get; set; } = true;

    /// <summary>Ask before switching the model of a conversation that has history (its cache does not carry over).</summary>
    public bool ConfirmModelSwitch { get; set; } = true;

    /// <summary>Closing the window hides it to the notification area instead; the tray icon's menu (or the app menu) exits.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>A Windows notification when a session finishes or needs an answer while Nomad.ai is in the background.</summary>
    public bool NotifyWhenDone { get; set; } = true;

    /// <summary>The taskbar button flashes then, until the window is brought up.</summary>
    public bool FlashTaskbar { get; set; } = true;

    // Claude Code's slash commands, as the last CLI listed them: offered when "/" is typed before a process runs.
    public List<Protocol.SlashCommand> SlashCommands { get; set; } = [];

    // Claude Code
    public string CliPath { get; set; } = "claude";
    public string DefaultModel { get; set; } = "claude-opus-5-5";

    /// <summary>default | acceptEdits | plan | bypassPermissions.</summary>
    public string DefaultPermissionMode { get; set; } = "default";

    /// <summary>low | medium | high | xhigh | max; empty = leave it to the CLI. Medium as in the original app.</summary>
    public string DefaultEffort { get; set; } = "medium";

    /// <summary>The effort last chosen with each model (model id → effort); a model without one starts at high.</summary>
    public Dictionary<string, string> EffortByModel { get; set; } = [];

    // Remote Control
    /// <summary>Sessions start with Remote Control on, so they can be continued from claude.ai or the phone.</summary>
    public bool RemoteControlByDefault { get; set; } = true;

    /// <summary>Folders where Nomad keeps a <c>claude remote-control</c> server, so the phone can start sessions there.</summary>
    public List<string> PhoneFolders { get; set; } = [];

    /// <summary>Session id → Remote Control bridge session, so a resumed session keeps its link (newest last, max 50).</summary>
    public Dictionary<string, string> RemoteControlBridges { get; set; } = [];

    // Updates (only a Setup install updates itself)

    /// <summary>
    /// Check GitHub at startup and every 6 hours, download a new version in the background and restart into it
    /// when no turn is running. Off: only check and offer it.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Offer pre-releases as updates too.</summary>
    public bool IncludePrereleases { get; set; }

    // Account (shown in the sidebar footer; the plan is remembered from the CLI's initialize answer)
    public string? DisplayName { get; set; }
    public string? LastPlan { get; set; }

    // Sidebar session list (its sliders menu)
    /// <summary>Date | Recent (last 2 / 8 / 24 hours, then each day) | Folder | None: how the Idle list is grouped.</summary>
    public string SessionGrouping { get; set; } = "Date";

    /// <summary>Archived sessions are listed too, in a group of their own.</summary>
    public bool ShowArchived { get; set; }

    /// <summary>Session ids archived from the sidebar: hidden from the list, their transcripts kept.</summary>
    public List<string> ArchivedSessions { get; set; } = [];

    /// <summary>Sessions untouched for <see cref="AutoArchiveHours"/> are archived (not while alive or open).</summary>
    public bool AutoArchive { get; set; } = true;

    /// <summary>How long a session stays untouched before it is archived (at least 1 hour).</summary>
    public int AutoArchiveHours { get; set; } = 24;

    /// <summary>
    /// Session id → when it was unarchived by hand: that counts as touching it, so auto-archive waits again from
    /// then. Dropped once the session is archived again.
    /// </summary>
    public Dictionary<string, DateTime> UnarchivedUtc { get; set; } = [];

    /// <summary>Collapsed Idle groups, as "Grouping:Title" ("Date:Yesterday", "Folder:Nomad.ai", "Archived").</summary>
    public List<string> CollapsedGroups { get; set; } = [];

    /// <summary>The Alive section is folded to its heading.</summary>
    public bool AliveCollapsed { get; set; }

    /// <summary>The Idle section is folded to its heading.</summary>
    public bool IdleCollapsed { get; set; }

    // Window and session state
    public bool SidebarCollapsed { get; set; }

    /// <summary>
    /// Sessions whose turn is running. Nomad's CLIs die with it, so if it goes down mid-turn (a restart, a crash) these
    /// are resumed at the next start and told to continue.
    /// </summary>
    public List<string> InterruptedSessions { get; set; } = [];

    /// <summary>The right-hand panel (project, context, usage, MCP servers) is shown.</summary>
    public bool SidePanelOpen { get; set; }

    /// <summary>The sidebar's width, dragged at its right edge (240–560).</summary>
    public double SidebarWidth { get; set; } = 375;

    /// <summary>The side panel's width, dragged at its left edge (240–560).</summary>
    public double SidePanelWidth { get; set; } = 296;

    /// <summary>Side panel cards turned off in its menu ("Project", "Subagents", "Context", "Usage", "Mcp").</summary>
    public List<string> HiddenPanelCards { get; set; } = [];
    public List<string> RecentFolders { get; set; } = [];

    /// <summary>Folders pinned on the new-session page: always shown there, first, in pin order.</summary>
    public List<string> PinnedFolders { get; set; } = [];

    /// <summary>New sessions in a git repository start in a new worktree of it.</summary>
    public bool NewSessionWorktree { get; set; }
    public string? LastFolder { get; set; }

    /// <summary>What was typed on the new-session page and not sent, put back at the next start.</summary>
    public string? NewSessionDraft { get; set; }
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }

    public NomadSettings Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, SettingsJsonContext.Default.NomadSettings), SettingsJsonContext.Default.NomadSettings)!;

    /// <summary>Remembers a session's Remote Control bridge, dropping the oldest beyond 50.</summary>
    public void RememberBridge(string sessionId, string bridgeSessionId)
    {
        RemoteControlBridges.Remove(sessionId);
        RemoteControlBridges[sessionId] = bridgeSessionId;
        while (RemoteControlBridges.Count > 50)
        {
            RemoteControlBridges.Remove(RemoteControlBridges.Keys.First());
        }
    }

    /// <summary>Moves a folder to the front of the recent list (max 10).</summary>
    public void TouchRecentFolder(string folder)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > 10)
        {
            RecentFolders.RemoveRange(10, RecentFolders.Count - 10);
        }
    }
}

/// <summary>Loads and saves <see cref="NomadSettings"/>.</summary>
public static class SettingsStore
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nomad", "settings.json");

    public static NomadSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.NomadSettings) ?? new NomadSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file falls back to defaults; it is overwritten on the next save.
        }

        return new NomadSettings();
    }

    public static void Save(NomadSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.NomadSettings));
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NomadSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
