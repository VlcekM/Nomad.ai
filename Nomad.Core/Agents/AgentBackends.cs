using Nomad.Core.Codex;
using Nomad.Core.OpenCode;

namespace Nomad.Core.Agents;

/// <summary>An agent a session can run on.</summary>
public sealed record AgentBackendInfo(string Id, string Name, string Description);

/// <summary>The agents Nomad can drive. A session picks one when it starts and keeps it.</summary>
public static class AgentBackends
{
    public const string ClaudeCode = "claude-code";
    public const string OpenCode = "opencode";
    public const string Codex = "codex";

    public static readonly IReadOnlyList<AgentBackendInfo> All =
    [
        new(ClaudeCode, "Claude Code", "Anthropic's agent, on your Claude plan or API key"),
        new(Codex, "Codex", "OpenAI's agent, on your ChatGPT plan or API key"),
        new(OpenCode, "OpenCode", "Any provider OpenCode is connected to: OpenRouter, Anthropic, OpenCode Zen, …"),
    ];

    /// <summary>The id itself when known, else Claude Code (sessions stored before there was a choice).</summary>
    public static string Normalize(string? id) => All.Any(b => b.Id == id) ? id! : ClaudeCode;

    public static string Name(string? id) => All.FirstOrDefault(b => b.Id == id)?.Name ?? "Claude Code";

    public static IAgentBackend Create(string? id) => Normalize(id) switch
    {
        OpenCode => new OpenCodeBackend(),
        Codex => new CodexBackend(),
        _ => new ClaudeCodeBackend(),
    };
}
