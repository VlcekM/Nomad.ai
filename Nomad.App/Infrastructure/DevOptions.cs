using System.Globalization;

namespace Nomad.App.Infrastructure;

/// <summary>
/// Command-line hooks for testing without clicking:
/// <code>
/// --dev-cwd &lt;folder&gt;          folder of the initial new session
/// --dev-prompt "&lt;text&gt;"        send this prompt on start
/// --dev-model &lt;id&gt;             model for the initial session (tests use claude-haiku-5-5)
/// --dev-session &lt;id&gt;           open this stored session on start
/// --dev-screenshot &lt;png&gt;       render the window to a PNG and exit (off-screen, unfocused) …
/// --dev-after &lt;seconds&gt;        … this many seconds after start (default 5)
/// --dev-wait-turn               … or as soon as the first turn finished (capped by --dev-after)
/// --dev-size &lt;W&gt;x&lt;H&gt;           initial window size
/// --dev-menu model|mode|folder|branch|plus|usage|remote|update|list|alivelist|row   open a menu (the update prompt, the Idle or Alive list options, the first Idle row's context menu) before the screenshot
/// --dev-settings-open [claude|updates]  open the settings dialog (or that page) before the screenshot
/// --dev-settings &lt;path&gt;        settings file to use instead of %APPDATA%\Nomad\settings.json
/// --dev-lean                    run the CLI with a small tool set and no MCP servers/skills
/// --dev-phone                   start the phone folders' Remote Control servers (scripted runs skip them)
/// --dev-expand                  expand every tool group (to screenshot rows)
/// --dev-answer allow|session|deny|pick   answer every permission request automatically
///                               (questions get their first options; pick only selects them)
/// --dev-set textSize=Large      change an appearance setting live, after start (textSize, width, theme)
/// --dev-stop-after &lt;seconds&gt;   press Stop this long after the dev prompt was sent
/// --dev-key Ctrl+B &lt;seconds&gt;  press a key on the focused control this long after start
/// --dev-drag x1,y1,x2,y2 &lt;seconds&gt;  drag the mouse (left button) between two window points this long after start
/// --dev-drag-ctrl               hold Ctrl during --dev-drag (a Ctrl+click when it doesn't move)
/// --dev-compose "&lt;text&gt;"     put this text in the composer after start (\n for new lines)
/// --dev-search "&lt;query&gt;"     open the session search (Ctrl+T) with this query after start
/// --dev-log &lt;file&gt;           append the lines to and from the CLI to this file
/// --dev-cli-debug &lt;file&gt;     the CLI writes its debug log to this file
/// --dev-paste-image &lt;file&gt;   attach this image (or any file, as a chip) to the composer, as a paste would; repeatable
/// --dev-update-source &lt;dir&gt;   update from a local Velopack release folder instead of GitHub (an installed build)
/// --dev-update-after &lt;seconds&gt;  first update check this long after start (to catch the update chip folding in)
/// --dev-releases-repo &lt;o/r&gt;   list another repository's GitHub releases on the Updates page
/// </code>
/// </summary>
public sealed class DevOptions
{
    public string? Cwd { get; private set; }
    public string? Prompt { get; private set; }
    public string? Model { get; private set; }
    public string? SessionId { get; private set; }
    public string? ScreenshotPath { get; private set; }
    public double AfterSeconds { get; private set; } = 5;
    public bool WaitForTurn { get; private set; }
    public (int Width, int Height)? Size { get; private set; }
    public string? Menu { get; private set; }
    public bool SettingsOpen { get; private set; }

    /// <summary><c>--dev-queue "text"</c>: prompts put in the queue above the composer, as if typed while Claude worked.</summary>
    public List<string> Queue { get; } = [];

    /// <summary><c>--dev-side-panel</c>: the right-hand panel is open (without saving that).</summary>
    public bool SidePanel { get; private set; }
    public bool SettingsClaudePage { get; private set; }
    public bool SettingsUpdatesPage { get; private set; }
    public string? SettingsPath { get; private set; }
    public bool Lean { get; private set; }

    /// <summary>Appends every line to and from the CLI to this file ("    public bool Lean { get; private set; }gt; " sent, "    public bool Lean { get; private set; }lt; " received).</summary>
    public string? LogPath { get; private set; }

    /// <summary>Start the phone folders' Remote Control servers in a scripted run too (off by default).</summary>
    public bool Phone { get; private set; }
    public bool ExpandTools { get; private set; }
    public string? Answer { get; private set; }
    public List<(string Key, string Value)> Set { get; } = [];
    public double? StopAfterSeconds { get; private set; }
    public (string Gesture, double Seconds)? Key { get; private set; }
    public (double X1, double Y1, double X2, double Y2, double Seconds)? Drag { get; private set; }

    /// <summary><c>--dev-drag-ctrl</c>: the drag holds Ctrl (a Ctrl+click when it doesn't move).</summary>
    public bool DragCtrl { get; private set; }
    public List<string> PasteImages { get; } = [];
    public string? Compose { get; private set; }

    /// <summary><c>--dev-search "query"</c>: the session search is open with this query.</summary>
    public string? Search { get; private set; }
    public string? UpdateSource { get; private set; }
    public double? UpdateCheckAfterSeconds { get; private set; }
    public string? ReleasesRepository { get; private set; }

    public IReadOnlyList<string> ExtraCliArguments =>
    [
        .. Lean ? ["--strict-mcp-config", "--disable-slash-commands", "--tools", "Bash,Read,Write,Edit,Glob,Grep,AskUserQuestion"] : (string[])[],
        .. CliDebugPath is { } debug ? ["--debug-file", debug] : (string[])[],
    ];

    /// <summary>The CLI writes its debug log here (<c>--debug-file</c>).</summary>
    public string? CliDebugPath { get; private set; }

    public bool IsActive => Cwd is not null || Prompt is not null || ScreenshotPath is not null || SessionId is not null;

    public static DevOptions Parse(string[] args)
    {
        var o = new DevOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--dev-cwd": o.Cwd = Next(); break;
                case "--dev-prompt": o.Prompt = Next(); break;
                case "--dev-model": o.Model = Next(); break;
                case "--dev-session": o.SessionId = Next(); break;
                case "--dev-screenshot": o.ScreenshotPath = Next(); break;
                case "--dev-after":
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                    {
                        o.AfterSeconds = s;
                    }

                    break;
                case "--dev-wait-turn": o.WaitForTurn = true; break;
                case "--dev-size":
                    var parts = Next().Split('x', 'X');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                    {
                        o.Size = (w, h);
                    }

                    break;
                case "--dev-menu": o.Menu = Next(); break;
                case "--dev-side-panel": o.SidePanel = true; break;
                case "--dev-queue": o.Queue.Add(Next().Replace("\\n", "\n")); break;
                case "--dev-settings-open":
                    o.SettingsOpen = true;
                    if (i + 1 < args.Length && args[i + 1] is "claude" or "updates")
                    {
                        o.SettingsClaudePage = args[i + 1] == "claude";
                        o.SettingsUpdatesPage = args[i + 1] == "updates";
                        i++;
                    }

                    break;
                case "--dev-update-source": o.UpdateSource = Path.GetFullPath(Next()); break;
                case "--dev-update-after":
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var check))
                    {
                        o.UpdateCheckAfterSeconds = check;
                    }

                    break;
                case "--dev-releases-repo": o.ReleasesRepository = Next(); break;
                case "--dev-settings": o.SettingsPath = Path.GetFullPath(Next()); break;
                case "--dev-lean": o.Lean = true; break;
                case "--dev-log": o.LogPath = Path.GetFullPath(Next()); break;
                case "--dev-cli-debug": o.CliDebugPath = Path.GetFullPath(Next()); break;
                case "--dev-phone": o.Phone = true; break;
                case "--dev-expand": o.ExpandTools = true; break;
                case "--dev-answer": o.Answer = Next(); break;
                case "--dev-key":
                    var gesture = Next();
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
                    {
                        o.Key = (gesture, at);
                    }
                    break;
                case "--dev-drag":
                    var xy = Next().Split(',').Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
                    if (xy.Length == 4 && !xy.Any(double.IsNaN) && double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dragAt))
                    {
                        o.Drag = (xy[0], xy[1], xy[2], xy[3], dragAt);
                    }

                    break;
                case "--dev-drag-ctrl": o.DragCtrl = true; break;
                case "--dev-compose": o.Compose = Next().Replace("\\n", "\n"); break;
                case "--dev-search": o.Search = Next(); break;
                case "--dev-paste-image": o.PasteImages.Add(Path.GetFullPath(Next())); break;
                case "--dev-stop-after":
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stop))
                    {
                        o.StopAfterSeconds = stop;
                    }

                    break;
                case "--dev-set":
                    var pair = Next().Split('=', 2);
                    if (pair.Length == 2)
                    {
                        o.Set.Add((pair[0], pair[1]));
                    }

                    break;
            }
        }

        return o;
    }
}
