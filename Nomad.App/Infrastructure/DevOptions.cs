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
/// --dev-menu model|mode|folder|plus|usage   open a menu before the screenshot
/// --dev-settings-open [claude]  open the settings dialog (or its Claude Code page) before the screenshot
/// --dev-settings &lt;path&gt;        settings file to use instead of %APPDATA%\Nomad\settings.json
/// --dev-lean                    run the CLI with a small tool set and no MCP servers/skills
/// --dev-expand                  expand every tool group (to screenshot rows)
/// --dev-answer allow|session|deny|pick   answer every permission request automatically
///                               (questions get their first options; pick only selects them)
/// --dev-set textSize=Large      change an appearance setting live, after start (textSize, width)
/// --dev-stop-after &lt;seconds&gt;   press Stop this long after the dev prompt was sent
/// --dev-key Ctrl+B &lt;seconds&gt;  press a key on the focused control this long after start
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
    public bool SettingsClaudePage { get; private set; }
    public string? SettingsPath { get; private set; }
    public bool Lean { get; private set; }
    public bool ExpandTools { get; private set; }
    public string? Answer { get; private set; }
    public List<(string Key, string Value)> Set { get; } = [];
    public double? StopAfterSeconds { get; private set; }
    public (string Gesture, double Seconds)? Key { get; private set; }

    public IReadOnlyList<string> ExtraCliArguments => Lean
        ? ["--strict-mcp-config", "--disable-slash-commands", "--tools", "Bash,Read,Write,Edit,Glob,Grep,AskUserQuestion"]
        : [];

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
                case "--dev-settings-open":
                    o.SettingsOpen = true;
                    if (i + 1 < args.Length && args[i + 1] == "claude")
                    {
                        o.SettingsClaudePage = true;
                        i++;
                    }

                    break;
                case "--dev-settings": o.SettingsPath = Next(); break;
                case "--dev-lean": o.Lean = true; break;
                case "--dev-expand": o.ExpandTools = true; break;
                case "--dev-answer": o.Answer = Next(); break;
                case "--dev-key":
                    var gesture = Next();
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
                    {
                        o.Key = (gesture, at);
                    }
                    break;
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
