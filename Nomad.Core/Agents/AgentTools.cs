using System.Text.Json;
using System.Text.Json.Nodes;
using static Nomad.Core.Agents.NomadSessionServer;

namespace Nomad.Core.Agents;

/// <summary>
/// What the agent tools need from the app: the calling chat, and its transcript and notifications. The app implements it;
/// <c>callerKey</c> is the calling chat's <see cref="AgentStartOptions.ChatKey"/>.
/// </summary>
public interface IAgentDesk
{
    /// <summary>
    /// The calling chat, when it is one this Nomad started and still has open; null otherwise (the tools then refuse).
    /// </summary>
    Task<DeskChat?> FindAsync(string? callerKey);

    /// <summary>
    /// Shows the file (already copied to Nomad's kept folder) in the calling chat's transcript, under the caption.
    /// </summary>
    Task ShowFileAsync(string callerKey, string path, string? caption);

    /// <summary>
    /// Notifies the user from the calling chat; returns where the notification went, for the agent.
    /// </summary>
    Task<string> NotifyAsync(string callerKey, string title, string message);
}

/// <summary>
/// A chat calling the agent tools: what it is, its permission mode as Nomad names it (<c>default</c>, <c>acceptEdits</c>,
/// <c>auto</c>, <c>plan</c>, <c>bypassPermissions</c>), and a token cancelled when the user stops its turn.
/// </summary>
public sealed record DeskChat(ChatInfo Info, string? Mode, CancellationToken Stopped = default);

/// <summary>
/// The tools Nomad itself serves to agents that lack them (Antigravity and Codex, through <see cref="ChatBridge"/>): a screenshot,
/// a PowerShell command run on the user's desktop, a file handed to the user, and a notification. They run in the Nomad window's
/// own process, which is on the desktop the user sees; an agent's shell, and the bridge process it starts, may be on a hidden one.
/// Claude Code has its own, and is not offered these.
/// </summary>
public static class AgentTools
{
    /// <summary>
    /// The screenshot tool's name on the server.
    /// </summary>
    public const string ScreenshotName = "take_screenshot";

    /// <summary>
    /// The desktop command tool's name on the server.
    /// </summary>
    public const string RunName = "run_on_desktop";

    /// <summary>
    /// The file-sending tool's name on the server.
    /// </summary>
    public const string SendFileName = "send_file";

    /// <summary>
    /// The notification tool's name on the server.
    /// </summary>
    public const string NotifyName = "notify";

    /// <summary>
    /// The largest file <c>send_file</c> takes.
    /// </summary>
    public const long MaxFileBytes = 100L * 1024 * 1024;

    /// <summary>
    /// The largest screenshot also returned as an image in the result (every one is saved as a file).
    /// </summary>
    public const int MaxInlineImageBytes = 3 * 1024 * 1024;

    /// <summary>
    /// How long screenshots are kept before a later one clears them away.
    /// </summary>
    public static readonly TimeSpan ScreenshotLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// The app's side; null until the app sets it (the tools then answer that they are not available).
    /// </summary>
    public static IAgentDesk? Desk { get; set; }

    /// <summary>
    /// Where screenshots and sent files are kept (Nomad's own data folder unless a test points it elsewhere).
    /// </summary>
    public static string KeptRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nomad", "agent-files");

    /// <summary>
    /// Takes the screenshot as a PNG of the given rectangle; a test replaces it.
    /// </summary>
    internal static Func<int, int, int, int, byte[]> Capture { get; set; } = DesktopCapture.CapturePng;

    /// <summary>
    /// The monitors; a test replaces them.
    /// </summary>
    internal static Func<IReadOnlyList<MonitorInfo>> ListMonitors { get; set; } = DesktopCapture.Monitors;

    /// <summary>
    /// Whether <paramref name="name"/> is one of these tools, as the server names them.
    /// </summary>
    public static bool Owns(string? name) => name is ScreenshotName or RunName or SendFileName or NotifyName;

    /// <summary>
    /// The tools as tools/list describes them.
    /// </summary>
    public static JsonNode[] Descriptors() =>
    [
        JsonNode.Parse("""
            {"name":"take_screenshot","description":"Take a screenshot of the user's real screen (your own shell runs on a hidden desktop, so screen captures from it fail). Saves a PNG and returns its path, which you can open with your file-reading tool, and the monitors' layout; the picture is also returned when it is small enough. All monitors by default, or one.","inputSchema":{"type":"object","properties":{"monitor":{"type":"integer","minimum":1,"description":"One monitor's number (1 is the leftmost); omit for all of them. The result lists the monitors."}},"additionalProperties":false}}
            """)!,
        JsonNode.Parse("""
            {"name":"run_on_desktop","description":"Run a Windows PowerShell command on the user's real desktop, from Nomad's own process. Use it for what fails from your own shell because that runs on a hidden desktop: focusing or reading windows, clicking or typing (pyautogui), screen captures, opening programs the user should see. Returns the exit code, output and errors. Not for ordinary work, which your own shell does. It runs outside any sandbox, so it works only while this chat's permission mode is Bypass permissions.","inputSchema":{"type":"object","properties":{"command":{"type":"string","description":"The PowerShell command or script."},"working_folder":{"type":"string","description":"The folder to run in; defaults to this chat's folder."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":600,"description":"How long to wait before stopping it: 60 by default, at most 600."}},"required":["command"],"additionalProperties":false}}
            """)!,
        JsonNode.Parse("""
            {"name":"send_file","description":"Hand the user a file: Nomad keeps a copy and shows it in this chat, an image as a picture and any other file as an item the user can open or save. Use it to show the user a picture or give them something you made. Up to 100 MB; not a folder.","inputSchema":{"type":"object","properties":{"path":{"type":"string","description":"The file, preferably an absolute path (a relative one is taken from this chat's folder)."},"caption":{"type":"string","description":"A line shown with it."}},"required":["path"],"additionalProperties":false}}
            """)!,
        JsonNode.Parse("""
            {"name":"notify","description":"Notify the user with a Windows notification and a line in this chat, which the phone view shows too. Use it when something needs their attention while they are away from this chat: a long task finished, a decision is needed, something failed.","inputSchema":{"type":"object","properties":{"title":{"type":"string","description":"A few words."},"message":{"type":"string","description":"One or two sentences."}},"required":["title","message"],"additionalProperties":false}}
            """)!,
    ];

    /// <summary>
    /// Runs one tool call for the chat with <paramref name="callerKey"/>; refused for any chat this Nomad did not start.
    /// </summary>
    public static async Task<JsonObject> CallAsync(string? name, JsonElement parameters, string? callerKey, CancellationToken cancellationToken = default)
    {
        if (Desk is not { } desk) return ToolResult("Nomad's desktop tools are not available here.", error: true);
        if (callerKey is null || await desk.FindAsync(callerKey).ConfigureAwait(false) is not { } caller)
            return ToolResult("These tools work only for a chat Nomad started and has open.", error: true);

        var chat = caller.Info;
        if (name == RunName && !AllowsDesktopCommands(caller.Mode))
        {
            AgentToolLog.Write($"run_on_desktop for \"{chat.Title}\" refused in permission mode {ModeLabel(caller.Mode)}");
            return ToolResult($"run_on_desktop is not available in this chat's permission mode, {ModeLabel(caller.Mode)}. It runs " +
                "commands on the user's desktop outside any sandbox, so Nomad allows it only in Bypass permissions mode, where the " +
                "chat already runs any command without asking. If it is needed, ask the user to switch this chat to Bypass permissions.",
                error: true);
        }

        var input = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var arguments) ? arguments : default;
        try
        {
            return name switch
            {
                ScreenshotName => await ScreenshotAsync(input, chat).ConfigureAwait(false),
                RunName => await RunAsync(input, chat, cancellationToken, caller.Stopped).ConfigureAwait(false),
                SendFileName => await SendFileAsync(input, chat, callerKey, desk).ConfigureAwait(false),
                NotifyName => await NotifyAsync(input, chat, callerKey, desk).ConfigureAwait(false),
                _ => ToolResult($"Unknown tool: {name}.", error: true),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentToolLog.Write($"{name} for \"{chat.Title}\" failed: {ex.Message}");
            return ToolResult($"{name} failed: {ex.Message}", error: true);
        }
    }

    /// <summary>
    /// Whether a chat in <paramref name="mode"/> may use <c>run_on_desktop</c>: only when the mode already lets the agent run any
    /// command, unsandboxed, without asking. For Codex that is Bypass permissions alone (Manual and Plan are read-only sandboxes;
    /// Accept edits and Auto run commands only inside the folder's sandbox, asking or having a reviewer decide past it). For
    /// Antigravity it is Bypass permissions alone too (Manual asks before commands, Accept edits approves only file edits, Auto
    /// only the commands it judges safe, Plan only plans).
    /// </summary>
    public static bool AllowsDesktopCommands(string? mode) => mode == "bypassPermissions";

    /// <summary>
    /// A permission mode as Nomad's mode menu names it, for messages.
    /// </summary>
    private static string ModeLabel(string? mode) => mode switch
    {
        "default" => "Manual",
        "acceptEdits" => "Accept edits",
        "auto" => "Auto",
        "plan" => "Plan",
        "bypassPermissions" => "Bypass permissions",
        null or "" => "unknown",
        _ => mode,
    };

    #region take_screenshot

    /// <summary>
    /// Captures every monitor, or the one asked for, into a PNG in the kept folder.
    /// </summary>
    private static Task<JsonObject> ScreenshotAsync(JsonElement input, ChatInfo chat)
    {
        var monitors = ListMonitors();
        int left, top, width, height;
        string what;
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("monitor", out var number) && number.ValueKind != JsonValueKind.Null)
        {
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var n) || monitors.FirstOrDefault(m => m.Number == n) is not { } monitor)
            {
                return Task.FromResult(ToolResult($"There is no monitor {number.GetRawText()}. The monitors are:\n{Layout(monitors)}", error: true));
            }

            (left, top, width, height) = (monitor.Left, monitor.Top, monitor.Width, monitor.Height);
            what = $"monitor {monitor.Number}";
        }
        else
        {
            if (monitors.Count == 0) return Task.FromResult(ToolResult("No monitors were found to capture.", error: true));

            left = monitors.Min(m => m.Left);
            top = monitors.Min(m => m.Top);
            width = monitors.Max(m => m.Left + m.Width) - left;
            height = monitors.Max(m => m.Top + m.Height) - top;
            what = monitors.Count == 1 ? "the screen" : $"all {monitors.Count} monitors";
        }

        return Task.Run(() =>
        {
            var png = Capture(left, top, width, height);
            var folder = Path.Combine(KeptRoot, "screenshots");
            Directory.CreateDirectory(folder);
            ClearOldScreenshots(folder);
            // The random part keeps two screenshots in the same millisecond apart; CreateNew never overwrites one.
            var path = Path.Combine(folder, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Unique()}.png");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(png);
            }
            AgentToolLog.Write($"take_screenshot for \"{chat.Title}\": {what}, {width}x{height}, {path}");

            var text = $"Saved a screenshot of {what} ({width}x{height} pixels, top left at {left},{top} on the desktop) to {path}\n" +
                $"Monitors:\n{Layout(monitors)}";
            var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
            if (png.Length <= MaxInlineImageBytes)
            {
                content.Add((JsonNode)new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
            }
            else
            {
                content[0]!["text"] = text + "\nThe picture is too large to return here: open the file to see it.";
            }

            return new JsonObject { ["content"] = content, ["isError"] = false };
        });
    }

    /// <summary>
    /// The monitors, one per line.
    /// </summary>
    private static string Layout(IReadOnlyList<MonitorInfo> monitors) =>
        monitors.Count == 0 ? "(none found)" : string.Join("\n", monitors.Select(m => "- " + m));

    /// <summary>
    /// Deletes screenshots older than <see cref="ScreenshotLifetime"/>, so the folder never grows without bound.
    /// </summary>
    private static void ClearOldScreenshots(string folder)
    {
        var cutoff = DateTime.UtcNow - ScreenshotLifetime;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("screenshot-*.png"))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff) file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open elsewhere: the next one tries again.
            }
        }
    }

    #endregion

    #region run_on_desktop

    /// <summary>
    /// Runs the command in PowerShell on the user's desktop and reports how it went.
    /// </summary>
    private static async Task<JsonObject> RunAsync(JsonElement input, ChatInfo chat, CancellationToken cancellationToken, CancellationToken stopped)
    {
        if (Str(input, "command") is not { Length: > 0 } command) return ToolResult("Give the command to run.", error: true);

        var timeout = DesktopCommand.DefaultTimeout;
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("timeout_seconds", out var seconds) && seconds.ValueKind != JsonValueKind.Null)
        {
            if (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out var s) || s < 1)
                return ToolResult("timeout_seconds must be a whole number of seconds, 1 or more.", error: true);

            timeout = TimeSpan.FromSeconds(Math.Min(s, (int)DesktopCommand.MaxTimeout.TotalSeconds));
        }

        // A relative folder is taken from the chat's; none given is the chat's own, else the user's home.
        var folder = Str(input, "working_folder") is { Length: > 0 } given
            ? Path.IsPathRooted(given) || chat.Folder is null ? given : Path.Combine(chat.Folder, given)
            : chat.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(folder)) return ToolResult($"The working folder does not exist: {folder}", error: true);

        AgentToolLog.Write($"run_on_desktop for \"{chat.Title}\" in {folder} (limit {timeout.TotalSeconds:0} s): {Shorten(command, 2000)}");
        // Stopped early by the call's own cancellation (the agent cancelled it, or its bridge went away) or by the user stopping the turn.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopped);
        var result = await DesktopCommand.RunAsync(command, folder, timeout, stop.Token).ConfigureAwait(false);
        AgentToolLog.Write($"run_on_desktop for \"{chat.Title}\" ended: " +
            (result.TimedOut ? "stopped at the time limit" : result.Cancelled ? "stopped early (cancelled)" : $"exit code {result.ExitCode}") +
            $" after {result.Elapsed.TotalSeconds:0.0} s");

        var text = (result.TimedOut
                ? $"Stopped after {timeout.TotalSeconds:0} seconds, the time limit, with everything it started."
                : result.Cancelled
                    ? "Stopped before it finished, with everything it started: the user stopped the turn or the call was cancelled."
                    : $"Exit code: {result.ExitCode}") +
            $"\n\nOutput:\n{(result.Output.Length > 0 ? result.Output.TrimEnd() : "(none)")}" +
            $"\n\nErrors:\n{(result.Errors.Length > 0 ? result.Errors.TrimEnd() : "(none)")}";
        return ToolResult(text, error: result.TimedOut || result.Cancelled || result.ExitCode != 0);
    }

    /// <summary>
    /// The text cut to <paramref name="max"/> characters, for the log.
    /// </summary>
    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    #endregion

    #region send_file

    /// <summary>
    /// Copies the file into the kept folder and shows the copy in the calling chat.
    /// </summary>
    private static async Task<JsonObject> SendFileAsync(JsonElement input, ChatInfo chat, string callerKey, IAgentDesk desk)
    {
        if (Str(input, "path") is not { Length: > 0 } given) return ToolResult("Give the path of the file to send.", error: true);

        string path;
        try
        {
            path = Path.GetFullPath(chat.Folder is { } folder ? Path.Combine(folder, given) : given);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ToolResult($"Not a usable path: {given} ({ex.Message})", error: true);
        }

        if (Directory.Exists(path)) return ToolResult($"{path} is a folder. Send the files in it one by one, or zip it first.", error: true);
        if (!File.Exists(path)) return ToolResult($"No file at {path}.", error: true);

        var size = new FileInfo(path).Length;
        if (size > MaxFileBytes)
            return ToolResult($"{path} is {Megabytes(size)}; send_file takes up to {Megabytes(MaxFileBytes)}.", error: true);

        var kept = KeptPath(Path.GetFileName(path));
        await Task.Run(() => File.Copy(path, kept)).ConfigureAwait(false);
        var caption = Str(input, "caption") is { Length: > 0 } c ? c : null;
        await desk.ShowFileAsync(callerKey, kept, caption).ConfigureAwait(false);
        AgentToolLog.Write($"send_file for \"{chat.Title}\": {path} ({size} bytes) kept as {kept}");

        return ToolResult($"Sent {Path.GetFileName(path)} ({Megabytes(size)}) to the user: it shows in this chat. {KeptMarker}{kept}");
    }

    /// <summary>
    /// What precedes the kept copy's path in a send_file result, so a transcript loaded later can show the file again.
    /// </summary>
    public const string KeptMarker = "Kept copy: ";

    /// <summary>
    /// The kept copy's path for <paramref name="name"/>: a new folder for each file, named for the time and a random part, so two
    /// calls in the same millisecond (Codex runs calls in parallel) never share one. The copy into it never overwrites, too.
    /// </summary>
    private static string KeptPath(string name)
    {
        var folder = Path.Combine(KeptRoot, "sent", $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Unique()}");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    /// <summary>
    /// A random part for kept file and folder names.
    /// </summary>
    private static string Unique() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// A size in megabytes, for messages.
    /// </summary>
    private static string Megabytes(long bytes) => bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    #endregion

    #region notify

    /// <summary>
    /// Notifies the user through the app.
    /// </summary>
    private static async Task<JsonObject> NotifyAsync(JsonElement input, ChatInfo chat, string callerKey, IAgentDesk desk)
    {
        if (Str(input, "title") is not { Length: > 0 } title || Str(input, "message") is not { Length: > 0 } message)
            return ToolResult("Give a title and a message.", error: true);

        if (title.Length > 200) title = title[..200];
        if (message.Length > 2000) message = message[..2000];

        var where = await desk.NotifyAsync(callerKey, title, message).ConfigureAwait(false);
        AgentToolLog.Write($"notify for \"{chat.Title}\": {title}");
        return ToolResult(NotifiedMarker + where);
    }

    /// <summary>
    /// How a notify result starts, so a transcript loaded later can show the notification's line again.
    /// </summary>
    public const string NotifiedMarker = "Notified the user: ";

    #endregion
}

/// <summary>
/// The agent tools' log, %APPDATA%\Nomad\agent-tools.log: every call, and every command run on the desktop. Past 1 MB it starts
/// over.
/// </summary>
public static class AgentToolLog
{
    /// <summary>
    /// Past this the log starts over.
    /// </summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// Serialises the writes.
    /// </summary>
    private static readonly Lock Gate = new();

    /// <summary>
    /// The log file; a test points it elsewhere.
    /// </summary>
    public static string FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nomad", "agent-tools.log");

    /// <summary>
    /// Appends a line with the time.
    /// </summary>
    public static void Write(string what)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Delete(FilePath);

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {what.ReplaceLineEndings(" ")}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that can't be written is no reason to fail the call.
        }
    }
}
