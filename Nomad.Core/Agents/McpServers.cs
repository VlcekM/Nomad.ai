using System.Text.Json;

namespace Nomad.Core.Agents;

/// <summary>An MCP server and its state: connected | failed | needs-auth | pending, or "configured" before a CLI ran.</summary>
public sealed record McpServerInfo(string Name, string Status);

/// <summary>
/// The MCP servers Claude Code would load for a folder, read from its config before a session starts: user scope
/// (<c>~/.claude.json</c> <c>mcpServers</c>), local scope (that file's entry for the folder) and project scope
/// (<c>.mcp.json</c> in the folder). claude.ai connectors and plugins' servers only show once the CLI reports them.
/// </summary>
public static class McpServers
{
    public static string ConfigPath =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, ".claude.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public static Task<IReadOnlyList<McpServerInfo>> ConfiguredAsync(string? folder) => Task.Run(() => Configured(folder));

    public static IReadOnlyList<McpServerInfo> Configured(string? folder)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(ConfigPath))
            {
                using var config = JsonDocument.Parse(File.ReadAllBytes(ConfigPath));
                AddNames(config.RootElement, names);
                if (folder is not null && config.RootElement.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Object)
                {
                    var key = folder.TrimEnd('\\', '/');
                    foreach (var project in projects.EnumerateObject())
                    {
                        if (string.Equals(project.Name.Replace('/', '\\').TrimEnd('\\'), key.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
                        {
                            AddNames(project.Value, names);
                        }
                    }
                }
            }

            if (folder is not null && Path.Combine(folder, ".mcp.json") is var local && File.Exists(local))
            {
                using var project = JsonDocument.Parse(File.ReadAllBytes(local));
                AddNames(project.RootElement, names);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Best effort: what could be read.
        }

        return names.Select(n => new McpServerInfo(n, "configured")).ToList();
    }

    private static void AddNames(JsonElement scope, SortedSet<string> names)
    {
        if (scope.ValueKind == JsonValueKind.Object && scope.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
        {
            foreach (var server in servers.EnumerateObject())
            {
                names.Add(server.Name);
            }
        }
    }
}
