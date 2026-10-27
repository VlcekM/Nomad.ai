using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klodi.Core.Settings;

/// <summary>User preferences, persisted as JSON in <c>%APPDATA%\Klodi\settings.json</c>.</summary>
public sealed class KlodiSettings
{
    // General › Appearance
    public string InterfaceFont { get; set; } = "Inter";
    public string CodeFont { get; set; } = "Cascadia Mono";

    /// <summary>Small | Medium | Large (transcript body 14 / 15 / 16 px).</summary>
    public string TextSize { get; set; } = "Medium";

    /// <summary>Narrow | Medium | Wide (760 / 1000 / 1280 px column; Wide matches the original).</summary>
    public string TranscriptWidth { get; set; } = "Wide";

    /// <summary>
    /// Draw the window with the GPU (ANGLE over Direct3D). Off uses Skia's software renderer, which
    /// needs far less memory for a text UI. Read at startup, so a change applies after a restart.
    /// </summary>
    public bool HardwareAcceleration { get; set; }

    // Claude Code
    public string CliPath { get; set; } = "claude";
    public string DefaultModel { get; set; } = "claude-opus-5-5";

    /// <summary>default | acceptEdits | plan | bypassPermissions.</summary>
    public string DefaultPermissionMode { get; set; } = "default";

    /// <summary>low | medium | high | xhigh | max; empty = leave it to the CLI. Medium as in the original app.</summary>
    public string DefaultEffort { get; set; } = "medium";

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

    // Window and session state
    public bool SidebarCollapsed { get; set; }
    public List<string> RecentFolders { get; set; } = [];
    public string? LastFolder { get; set; }
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }

    public KlodiSettings Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, SettingsJsonContext.Default.KlodiSettings), SettingsJsonContext.Default.KlodiSettings)!;

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

/// <summary>Loads and saves <see cref="KlodiSettings"/>.</summary>
public static class SettingsStore
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klodi", "settings.json");

    public static KlodiSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.KlodiSettings) ?? new KlodiSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file falls back to defaults; it is overwritten on the next save.
        }

        return new KlodiSettings();
    }

    public static void Save(KlodiSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.KlodiSettings));
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KlodiSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
