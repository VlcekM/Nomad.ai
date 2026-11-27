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

    /// <summary>The code editor's text size in points (Visual Studio's 10 by default).</summary>
    public int EditorFontSize { get; set; } = 10;

    /// <summary>Small | Medium | Large (sidebar, menus, status row: 12 / 13 / 14 px).</summary>
    public string InterfaceTextSize { get; set; } = "Medium";

    /// <summary>Dark | Light | System (follows the Windows app mode).</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>The color theme: "nomad" (built in) or a file in the themes folder next to settings.json, by name.</summary>
    public string ColorTheme { get; set; } = "nomad";

    /// <summary>Narrow | Medium | Wide (760 / 1000 / 1280 px column; Wide matches the original).</summary>
    public string TranscriptWidth { get; set; } = "Wide";

    /// <summary>Show the activity lines (thinking, tool groups such as "Read a file"); off: only prompts and replies.</summary>
    public bool ShowActivity { get; set; } = true;

    /// <summary>Under each prompt, what the user's hooks added to it (UserPromptSubmit context); off by default.</summary>
    public bool ShowHookContext { get; set; }

    /// <summary>The ring at the end of the status row (plan usage; spins while a turn runs, opens the usage limits).</summary>
    public bool ShowUsageRing { get; set; } = true;

    /// <summary>Show the three providers' usage cards above the new-session project picker.</summary>
    public bool ShowNewSessionUsage { get; set; } = true;

    /// <summary>
    /// The provider IDs for the new-session usage cards, from left to right.
    /// </summary>
    public List<string>? NewSessionUsageCards { get; set; }

    /// <summary>The status row shows only the current model's chip; the other models and the effort slider move into its menu.</summary>
    public bool CompactModelPicker { get; set; }

    /// <summary>The size Claude's workflows aim for: small | medium | large | unrestricted; null for the CLI's default (medium).</summary>
    public string? WorkflowSize { get; set; }

    /// <summary>After a turn, the CLI guesses the next prompt; it shows as ghost text in an empty composer.</summary>
    public bool PromptSuggestions { get; set; } = true;

    /// <summary>Ask before switching the model of a conversation that has history (its cache does not carry over).</summary>
    public bool ConfirmModelSwitch { get; set; } = true;

    /// <summary>On the new-session screen, F1…F4 pick the model: Fable to Haiku, or OpenCode's first four favorites.</summary>
    public bool ModelFunctionKeys { get; set; }

    /// <summary>
    /// Reuse the window's size, position and maximized state at startup.
    /// </summary>
    public bool RememberWindowPlacement { get; set; } = true;

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

    // Agents
    /// <summary>The agent a new session runs on: claude-code | codex | opencode (Agents.AgentBackends).</summary>
    public string DefaultBackend { get; set; } = Agents.AgentBackends.ClaudeCode;

    /// <summary>The agent mostly used: its sessions show no logo in the sidebar, only the others' do. Null: every row shows its logo.</summary>
    public string? MainBackend { get; set; }

    // OpenCode
    public string OpenCodePath { get; set; } = "opencode";

    /// <summary>The OpenCode model last picked ("provider/model"); null: OpenCode's own default.</summary>
    public string? OpenCodeModel { get; set; }

    /// <summary>
    /// OpenCode models starred in the model menu ("provider/model"), in the order starred: the first four are the
    /// status row's chips and Ctrl+Shift+1…4 (and F1…F4), as Claude's four models are.
    /// </summary>
    public List<string> OpenCodeFavorites { get; set; } = [];

    // Codex
    /// <summary>The codex executable (or command name); "codex" finds it on PATH or where its installer put it.</summary>
    public string CodexPath { get; set; } = "codex";

    /// <summary>The Codex model last picked; null: Codex's own default.</summary>
    public string? CodexModel { get; set; }

    // Antigravity
    /// <summary>The Antigravity executable (or command name); "agy" finds it on PATH or where its installer put it.</summary>
    public string AntigravityPath { get; set; } = "agy";

    /// <summary>The Antigravity model last picked; null: Antigravity's default.</summary>
    public string? AntigravityModel { get; set; }

    /// <summary>
    /// The Antigravity sessions opened in Nomad, indexed by the CLI's session ID.
    /// </summary>
    public Dictionary<string, Sessions.SessionSummary> AntigravitySessions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Model IDs last advertised by the signed-in Antigravity CLI, so subscription-gated models stay hidden when unavailable.
    /// </summary>
    public List<string> AntigravityModels { get; set; } = [];

    // Remote Control
    /// <summary>Sessions start with Remote Control on, so they can be continued from claude.ai or the phone.</summary>
    public bool RemoteControlByDefault { get; set; } = true;

    /// <summary>Folders where Nomad keeps a <c>claude remote-control</c> server, so the phone can start sessions there.</summary>
    public List<string> PhoneFolders { get; set; } = [];

    /// <summary>Session id → Remote Control bridge session, so a resumed session keeps its link (newest last, max 50).</summary>
    public Dictionary<string, string> RemoteControlBridges { get; set; } = [];

    /// <summary>Session id to the folder approved in Nomad, overriding the transcript's original cwd.</summary>
    public Dictionary<string, string> MovedSessionFolders { get; set; } = [];

    // Nomad Hub (docs/hub.md)
    /// <summary>The hub's address; null: no hub, Nomad works on its own.</summary>
    public string? HubUrl { get; set; }

    /// <summary>This PC as the hub knows it, set by enrolling.</summary>
    public Guid? HubDeviceId { get; set; }

    public string? HubDeviceName { get; set; }

    /// <summary>The device secret, encrypted for this Windows user (DPAPI), base64.</summary>
    public string? HubSecret { get; set; }

    /// <summary>Sessions running here stream through the hub, so the other PCs can watch and prompt them (§8).</summary>
    public bool HubRelay { get; set; } = true;

    /// <summary>Extra workspace folders selected for each chat; the original working directory stays the same.</summary>
    public Dictionary<string, List<string>> SessionAdditionalDirectories { get; set; } = [];

    /// <summary>
    /// Folder to a text file that replaces Claude Code's system prompt (<c>--system-prompt-file</c>) for sessions in that
    /// folder or below it.
    /// </summary>
    public Dictionary<string, string> FolderSystemPrompts { get; set; } = [];

    /// <summary>Claude may suggest a task for a chat of its own (a card to start it), with a reminder sent along with prompts.</summary>
    public bool SuggestNewChats { get; set; } = true;

    /// <summary>Folders (and the folders inside them) where Claude never suggests a new chat.</summary>
    public List<string> NoNewChatFolders { get; set; } = [];

    /// <summary>
    /// The same for Codex (its base instructions). Null in settings saved before Codex had its own: Codex then follows
    /// <see cref="FolderSystemPrompts"/>, as it did.
    /// </summary>
    public Dictionary<string, string>? FolderCodexSystemPrompts { get; set; }

    // Updates (only a Setup install updates itself)

    /// <summary>
    /// Check GitHub at startup and every 6 hours, download a new version in the background and restart into it
    /// when no turn is running. Off: only check and offer it.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Offer pre-releases as updates too.</summary>
    public bool IncludePrereleases { get; set; }

    // Account (the plan is remembered from the CLI's initialize answer)
    public string? LastPlan { get; set; }

    /// <summary>
    /// The Claude models the CLI offers besides the menu's four (newer and older versions), as its initialize answer
    /// listed them last, so the model menu has them before a session's process runs.
    /// </summary>
    public List<KnownModel> ClaudeModels { get; set; } = [];

    // Sidebar session list (its sliders menu)
    /// <summary>Date | Recent (last 2 / 8 / 24 hours, then each day) | Folder | None: how the Idle list is grouped.</summary>
    public string SessionGrouping { get; set; } = "Date";

    /// <summary>Archived sessions are listed too, in a group of their own.</summary>
    public bool ShowArchived { get; set; }

    /// <summary>How the Alive list is grouped: the same choices as <see cref="SessionGrouping"/>.</summary>
    public string AliveGrouping { get; set; } = "None";

    /// <summary>Alive rows show their project's name on a second line.</summary>
    public bool AliveShowProject { get; set; }

    /// <summary>Folders pinned in the Alive list (grouped by folder): their group shows, with its +, even with no alive session.</summary>
    public List<string> AliveFolders { get; set; } = [];

    /// <summary>Session ids archived from the sidebar: hidden from the list, their transcripts kept.</summary>
    public List<string> ArchivedSessions { get; set; } = [];

    /// <summary>Session ids pinned in the sidebar: listed first, never ended for being idle nor auto-archived.</summary>
    public List<string> PinnedSessions { get; set; } = [];

    /// <summary>
    /// Session id to the session it was forked from (a side task Claude suggested, started from its card): the sidebar lists
    /// it under that one until it is unforked.
    /// </summary>
    public Dictionary<string, string> ForkedSessions { get; set; } = [];

    /// <summary>
    /// A suggested chat's card (its suggest_new_chat call's tool-use id) to the session started from it, so after a restart the
    /// card opens that session rather than starting another.
    /// </summary>
    public Dictionary<string, string> SuggestedChats { get; set; } = [];

    /// <summary>
    /// Session id to the GitHub pull request or issue URL it was started from on the Pull requests &amp; issues page,
    /// so sessions whose own stores don't record prompt URLs (Codex, OpenCode, Antigravity) keep their sidebar icon.
    /// </summary>
    public Dictionary<string, string> SessionWorkUrls { get; set; } = [];

    /// <summary>Sessions untouched for <see cref="AutoArchiveHours"/> are archived (not while alive or open).</summary>
    public bool AutoArchive { get; set; } = true;

    public const int DefaultAutoArchiveHours = 6;

    /// <summary>How long a session stays untouched before it is archived (at least 1 hour).</summary>
    public int AutoArchiveHours { get; set; } = DefaultAutoArchiveHours;

    /// <summary>
    /// An open session whose process has been quiet for <see cref="SleepAfterHours"/> has its process ended (it moves to Idle).
    /// Pinned sessions, and ones with background work running, are never ended.
    /// </summary>
    public bool SleepIdleSessions { get; set; } = true;

    /// <summary>How long a session's process may stay quiet before it is ended (1 hour to <see cref="MaxSleepAfterHours"/>).</summary>
    public int SleepAfterHours { get; set; } = 1;

    /// <summary>The longest <see cref="SleepAfterHours"/> (a year): longer is the same as never, and much longer can't be timed.</summary>
    public const int MaxSleepAfterHours = 8760;

    /// <summary>
    /// Session id → when it was unarchived by hand: that counts as touching it, so auto-archive waits again from
    /// then. Dropped once the session is archived again.
    /// </summary>
    public Dictionary<string, DateTime> UnarchivedUtc { get; set; } = [];

    /// <summary>
    /// Collapsed groups, as "Grouping:Title" ("Date:Yesterday", "Folder:Nomad.ai", "Archived"); the Alive list's
    /// with "Alive:" before it ("Alive:Folder:Nomad.ai").
    /// </summary>
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

    /// <summary>Chats have a Files view (the folder tree and code editor); off, the Chat / Files switch is gone and chats are chat only.</summary>
    public bool FilesViewEnabled { get; set; } = true;

    /// <summary>In a chat's Files view, the folder tree is on the right and the open file on the left; off, swapped.</summary>
    public bool FileTreeOnRight { get; set; } = true;

    /// <summary>The key that switches a chat between Chat and Files, as Avalonia writes a key gesture ("Tab", "Ctrl+Shift+E").</summary>
    public string FilesViewKey { get; set; } = "Tab";

    /// <summary>The Files view's tree width, dragged at its inner edge (180–720).</summary>
    public double FileTreeWidth { get; set; } = 280;

    /// <summary>The Files view's tree shows every file, build output (bin, obj) and dot folders too, as Visual Studio's "Show All Files".</summary>
    public bool ShowAllFiles { get; set; }

    /// <summary>Side panel cards turned off in its menu ("Project", "Subagents", "Tasks", "Context", "Usage", "Mcp").</summary>
    public List<string> HiddenPanelCards { get; set; } = [];
    public List<string> RecentFolders { get; set; } = [];

    /// <summary>Folders pinned on the new-session page: always shown there, first, in pin order.</summary>
    public List<string> PinnedFolders { get; set; } = [];

    /// <summary>Folders removed from the new-session page, with when (UTC): hidden there until a newer session in them.</summary>
    public Dictionary<string, DateTime> RemovedFolders { get; set; } = [];

    /// <summary>New sessions in a git repository start in a new worktree of it.</summary>
    public bool NewSessionWorktree { get; set; }

    /// <summary>
    /// The model the pull requests and issues page starts its sessions with (one of the default agent's chips); null for the
    /// agent's default model.
    /// </summary>
    public string? WorkPageModel { get; set; }

    /// <summary>
    /// The last ordering selected for each repository's pull requests and issues.
    /// </summary>
    public WorkSortOrder WorkPageSortOrder { get; set; }

    /// <summary>
    /// Adds the signed-in GitHub user to an issue when its Fix button starts a session.
    /// </summary>
    public bool AutoAssignIssuesOnFix { get; set; }

    /// <summary>
    /// Repositories pinned on the pull requests and issues page ("VlcekM/Nomad.ai" → the checkout it was pinned from): always
    /// listed first, by name, even with nothing open.
    /// </summary>
    public Dictionary<string, string> PinnedRepos { get; set; } = [];

    public string? LastFolder { get; set; }

    /// <summary>What was typed on the new-session page and not sent, put back at the next start.</summary>
    public string? NewSessionDraft { get; set; }
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Horizontal window position in screen pixels, when saved.
    /// </summary>
    public int? WindowX { get; set; }

    /// <summary>
    /// Vertical window position in screen pixels, when saved.
    /// </summary>
    public int? WindowY { get; set; }

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

    /// <summary>
    /// The Codex prompts, starting from the shared ones for settings saved before Codex had its own.
    /// </summary>
    public Dictionary<string, string> CodexSystemPrompts() =>
        FolderCodexSystemPrompts ??= new Dictionary<string, string>(FolderSystemPrompts, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The system prompt file for a session folder (Claude Code's, or Codex's): the entry for the folder itself or its
    /// nearest parent, when the file exists. A missing file means the agent's own prompt, so the session still starts; a
    /// parent folder's file does not stand in for it.
    /// </summary>
    public string? SystemPromptFor(string? folder, bool codex = false)
    {
        var prompts = codex ? FolderCodexSystemPrompts ?? FolderSystemPrompts : FolderSystemPrompts;
        if (string.IsNullOrWhiteSpace(folder) || prompts.Count == 0)
        {
            return null;
        }

        string Trim(string path) => path.Replace('/', '\\').TrimEnd('\\');
        var target = Trim(folder);
        var best = prompts
            .Where(entry => Trim(entry.Key) is { Length: > 0 } key
                && (string.Equals(target, key, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => Trim(entry.Key).Length)
            .Select(entry => entry.Value)
            .FirstOrDefault();
        return best is not null && File.Exists(best) ? best : null;
    }

    /// <summary>
    /// Whether Claude may suggest a task for a chat of its own: anywhere unless turned off (<see cref="SuggestNewChats"/>), except
    /// in a folder of <see cref="NoNewChatFolders"/> or one inside it.
    /// </summary>
    public bool SuggestsNewChatsIn(string? folder)
    {
        if (!SuggestNewChats)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            return true;
        }

        string Trim(string path) => path.Replace('/', '\\').TrimEnd('\\');
        var target = Trim(folder);
        return !NoNewChatFolders.Select(Trim).Any(key => key.Length > 0
            && (string.Equals(target, key, StringComparison.OrdinalIgnoreCase) || target.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Moves a folder to the front of the recent list (max 12).</summary>
    public void TouchRecentFolder(string folder)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        foreach (var key in RemovedFolders.Keys.Where(k => string.Equals(k.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList())
        {
            RemovedFolders.Remove(key);
        }

        if (RecentFolders.Count > 12)
        {
            RecentFolders.RemoveRange(12, RecentFolders.Count - 12);
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

/// <summary>
/// A model by its id and display name ("claude-fable-5-2", "Fable 5.2").
/// </summary>
public sealed class KnownModel
{
    /// <summary>
    /// The id passed to <c>--model</c>.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The name the CLI gives it.
    /// </summary>
    public string Name { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NomadSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// The ordering of pull requests and issues within their repository.
/// </summary>
public enum WorkSortOrder
{
    /// <summary>
    /// Most recently updated first.
    /// </summary>
    RecentlyUpdated,

    /// <summary>
    /// Highest issue or pull request number first.
    /// </summary>
    NumberDescending,

    /// <summary>
    /// Lowest issue or pull request number first.
    /// </summary>
    NumberAscending,
}
