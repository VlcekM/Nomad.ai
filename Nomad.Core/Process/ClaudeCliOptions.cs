namespace Nomad.Core.Process;

/// <summary>How to launch one <c>claude</c> CLI process for one session.</summary>
public sealed record ClaudeCliOptions
{
    /// <summary>Configured CLI path or command name; <c>claude</c> resolves through PATH.</summary>
    public string CliPath { get; init; } = "claude";

    /// <summary>The session folder; becomes the child's working directory.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Model id (e.g. <c>claude-haiku-5-5</c>); null keeps the CLI default.</summary>
    public string? Model { get; init; }

    /// <summary>default | acceptEdits | plan | bypassPermissions (null keeps the CLI default).</summary>
    public string? PermissionMode { get; init; }

    /// <summary>low | medium | high | xhigh | max (null keeps the CLI default).</summary>
    public string? Effort { get; init; }

    /// <summary>
    /// summarized | omitted (<c>--thinking-display</c>, a hidden CLI flag). In print mode the CLI
    /// omits thinking text by default, sending only a signature; summarized makes it readable.
    /// </summary>
    public string? ThinkingDisplay { get; init; }

    /// <summary>Existing session id to continue in this new process (<c>--resume</c>).</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>With a resume: keep the transcript only up to and including this record (<c>--resume-session-at</c>).</summary>
    public string? ResumeSessionAt { get; init; }

    /// <summary>With a resume: continue under a new session id (<c>--fork-session</c>).</summary>
    public bool ForkSession { get; init; }

    /// <summary>
    /// The id a new (or forked) session gets (<c>--session-id</c>). Known up front, it is in the process's command line,
    /// so another Nomad can tell the session is running (<see cref="Remote.RemoteSessionScanner.RunningSessionIds"/>).
    /// </summary>
    public string? NewSessionId { get; init; }

    /// <summary>Emit <c>stream_event</c> lines with raw API deltas.</summary>
    public bool IncludePartialMessages { get; init; } = true;

    /// <summary>
    /// Echo every user message on stdout (<c>--replay-user-messages</c>), ours with the uuid we sent,
    /// so prompts typed in Remote Control (claude.ai, the phone) show up too.
    /// </summary>
    public bool ReplayUserMessages { get; init; } = true;

    /// <summary>Route permission prompts to us as <c>can_use_tool</c> control requests.</summary>
    public bool PermissionPromptOverStdio { get; init; } = true;

    /// <summary>Allow switching into bypassPermissions later in the session.</summary>
    public bool AllowBypassPermissions { get; init; }

    /// <summary><c>--prompt-suggestions true</c>: a <c>prompt_suggestion</c> line after each turn.</summary>
    public bool PromptSuggestions { get; init; }

    /// <summary>Additional raw arguments appended at the end.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}
