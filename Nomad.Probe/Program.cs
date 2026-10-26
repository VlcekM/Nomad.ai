using System.Diagnostics;
using System.Text;
using Nomad.Core.Agents;
using Nomad.Core.Process;
using Nomad.Core.Sessions;

namespace Nomad.Probe;

/// <summary>
/// Protocol test bench: drives Nomad.Core against the real <c>claude</c> CLI.
/// <code>
/// Nomad.Probe &lt;scenario&gt; --cwd &lt;folder&gt; [--model id] [--out &lt;transcript.jsonl&gt;] [--full]
///   scenarios: pong | write-allow | write-deny | interrupt | multiturn | controls | sessions | history &lt;file&gt;
/// </code>
/// Unless <c>--full</c> is given, the CLI runs with a reduced tool set and no MCP servers or skills,
/// which keeps each test turn's prompt small.
/// </summary>
internal static class Program
{
    private static string _model = "claude-haiku-5-5";
    private static string? _out;
    private static bool _full;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.WriteLine("usage: Nomad.Probe <pong|write-allow|write-deny|interrupt|multiturn|controls|sessions|history> --cwd <folder> [--model id] [--out file] [--full]");
            return 2;
        }

        var scenario = args[0];
        string? cwd = null;
        string? file = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cwd": cwd = args[++i]; break;
                case "--model": _model = args[++i]; break;
                case "--out": _out = args[++i]; break;
                case "--full": _full = true; break;
                default: file = args[i]; break;
            }
        }

        cwd ??= Environment.CurrentDirectory;
        try
        {
            var ok = scenario switch
            {
                "pong" => await PongAsync(cwd),
                "write-allow" => await WriteAsync(cwd, allow: true),
                "write-deny" => await WriteAsync(cwd, allow: false),
                "interrupt" => await InterruptAsync(cwd),
                "multiturn" => await MultiTurnAsync(cwd),
                "controls" => await ControlsAsync(cwd),
                "title" => await TitleAsync(cwd),
                "sessions" => Sessions(),
                "history" => History(file ?? throw new ArgumentException("history needs a transcript path")),
                _ => throw new ArgumentException($"unknown scenario {scenario}"),
            };
            Console.WriteLine(ok ? "PROBE OK" : "PROBE FAILED");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PROBE ERROR: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            ClaudeCliProcess.KillAllOwned();
        }
    }

    // (a) one turn, plain text
    private static async Task<bool> PongAsync(string cwd)
    {
        await using var probe = await ProbeSession.StartAsync(cwd, null, "a");
        var result = await probe.TurnAsync("Reply with exactly: pong");
        return result is { IsError: false } && probe.LastText.Contains("pong", StringComparison.OrdinalIgnoreCase);
    }

    // (b) / (c) a Write that needs permission, allowed or denied
    private static async Task<bool> WriteAsync(string cwd, bool allow)
    {
        var target = Path.Combine(cwd, "probe.txt");
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        await using var probe = await ProbeSession.StartAsync(cwd, null, allow ? "b" : "c");
        probe.OnPermission = request =>
        {
            Console.WriteLine($"   -> answering {(allow ? "allow" : "deny")} for {request.ToolName}");
            return new PermissionDecision(allow ? PermissionChoice.Allow : PermissionChoice.Deny);
        };
        var result = await probe.TurnAsync("Create a file named probe.txt containing hello. Use the Write tool once; if it is denied, do not retry and just say so.");
        var exists = File.Exists(target);
        var content = exists ? File.ReadAllText(target) : null;
        Console.WriteLine($"   file exists={exists} content={content}");
        return result is not null && probe.PermissionCount >= 1 && (allow ? content?.Trim() == "hello" : !exists);
    }

    // (d) interrupt mid-turn, then show the process still takes a turn
    private static async Task<bool> InterruptAsync(string cwd)
    {
        await using var probe = await ProbeSession.StartAsync(cwd, null, "d");
        var sent = false;
        probe.OnText = text =>
        {
            if (!sent && text.Length > 20)
            {
                sent = true;
                Console.WriteLine("\n   -> sending interrupt");
                _ = probe.Backend.InterruptAsync();
            }
        };
        var result = await probe.TurnAsync("Count from 1 to 300, one number per line, no other text.");
        probe.OnText = null;
        Console.WriteLine($"   interrupted={result?.Interrupted} subtype={result?.Subtype} textLength={probe.LastText.Length}");
        var after = await probe.TurnAsync("Reply with exactly: after");
        return result is { Interrupted: true } && after is { IsError: false } && probe.LastText.Contains("after", StringComparison.OrdinalIgnoreCase);
    }

    // (e) two turns on one process, then --resume in a new process
    private static async Task<bool> MultiTurnAsync(string cwd)
    {
        string? sessionId;
        await using (var probe = await ProbeSession.StartAsync(cwd, null, "e1"))
        {
            await probe.TurnAsync("Remember the code word: kiwi. Reply with exactly: ok");
            var second = await probe.TurnAsync("What is the code word? Reply with just the word.");
            if (second is null || !probe.LastText.Contains("kiwi", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            sessionId = probe.Backend.SessionId;
            Console.WriteLine($"   session {sessionId}; stopping process {probe.Backend.Process?.ProcessId}");
        }

        await using var resumed = await ProbeSession.StartAsync(cwd, sessionId, "e2");
        var third = await resumed.TurnAsync("Say the code word once more. Reply with just the word.");
        Console.WriteLine($"   resumed session {resumed.Backend.SessionId} (same={resumed.Backend.SessionId == sessionId})");
        return third is { IsError: false } && resumed.LastText.Contains("kiwi", StringComparison.OrdinalIgnoreCase);
    }

    // live control requests (no model turn needed)
    private static async Task<bool> ControlsAsync(string cwd)
    {
        await using var probe = await ProbeSession.StartAsync(cwd, null, "f");
        await Task.Delay(1500);
        var mode = await probe.Backend.SetPermissionModeAsync("acceptEdits");
        var plan = await probe.Backend.SetPermissionModeAsync("plan");
        var back = await probe.Backend.SetPermissionModeAsync("default");
        var model = await probe.Backend.SetModelAsync("claude-haiku-5-5");
        var effort = await probe.Backend.SetEffortAsync("low");
        var bypass = await probe.Backend.SetPermissionModeAsync("bypassPermissions");
        Console.WriteLine($"   set_permission_mode acceptEdits={mode} plan={plan} default={back} bypass={bypass}");
        Console.WriteLine($"   set_model={model} apply_flag_settings(effort)={effort}");
        return mode && plan && back && model && effort;
    }

    // generate_session_title: the short titles the original app shows in the sidebar
    private static async Task<bool> TitleAsync(string cwd)
    {
        await using var probe = await ProbeSession.StartAsync(cwd, null, "g");
        await Task.Delay(1500);
        var sw = Stopwatch.StartNew();
        var title = await probe.Backend.GenerateTitleAsync("Fix the login button colour on the settings page and add a test for it", persist: false);
        Console.WriteLine($"   generate_session_title in {sw.ElapsedMilliseconds} ms: \"{title}\"");
        return title is not null;
    }

    private static bool Sessions()
    {
        var store = new SessionStore();
        var sw = Stopwatch.StartNew();
        var sessions = store.Scan();
        var cold = sw.ElapsedMilliseconds;
        sw.Restart();
        store.Scan();
        Console.WriteLine($"   {sessions.Count} sessions; cold scan {cold} ms, cached rescan {sw.ElapsedMilliseconds} ms");
        foreach (var s in sessions.Take(12))
        {
            Console.WriteLine($"   {s.LastActivityUtc:yyyy-MM-dd HH:mm}  {s.Title,-62} {s.WorkingDirectory}");
        }

        return sessions.Count > 0;
    }

    private static bool History(string file)
    {
        var sw = Stopwatch.StartNew();
        var history = SessionStore.LoadHistory(file);
        Console.WriteLine($"   {history.Items.Count} items in {sw.ElapsedMilliseconds} ms; truncated={history.Truncated} model={history.Model} cwd={history.WorkingDirectory}");
        foreach (var item in history.Items.Take(40))
        {
            var line = item switch
            {
                HistoryUserMessage u => "user: " + u.Text,
                HistoryAssistantText a => "assistant: " + a.Text,
                HistoryThinking t => "thinking: " + t.Text,
                HistoryToolCall c => $"tool_use {c.Name}: {c.Input}",
                HistoryToolResult r => $"tool_result{(r.IsError ? " (error)" : "")}: {r.Text}",
                HistoryNotice n => "notice: " + n.Text,
                _ => item.ToString() ?? "",
            };
            line = line.ReplaceLineEndings(" ");
            Console.WriteLine("   " + (line.Length > 150 ? line[..150] + "…" : line));
        }

        return history.Items.Count > 0;
    }

    /// <summary>One backend plus console logging and turn waiting.</summary>
    private sealed class ProbeSession : IAsyncDisposable
    {
        private readonly StringBuilder _text = new();
        private readonly StreamWriter? _transcript;
        private TaskCompletionSource<TurnResult?> _turn = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ProbeSession(ClaudeCodeBackend backend, StreamWriter? transcript)
        {
            Backend = backend;
            _transcript = transcript;
        }

        public ClaudeCodeBackend Backend { get; }

        public Func<PermissionRequest, PermissionDecision>? OnPermission { get; set; }

        public Action<string>? OnText { get; set; }

        public int PermissionCount { get; private set; }

        public string LastText => _text.ToString();

        public static async Task<ProbeSession> StartAsync(string cwd, string? resume, string tag)
        {
            Directory.CreateDirectory(cwd);
            StreamWriter? transcript = null;
            if (_out is not null)
            {
                var path = _out.Contains("{tag}") ? _out.Replace("{tag}", tag) : _out;
                transcript = new StreamWriter(path, append: false, new UTF8Encoding(false));
            }

            var backend = new ClaudeCodeBackend();
            var session = new ProbeSession(backend, transcript);
            backend.RawLine += session.OnRaw;
            backend.EventReceived += session.OnEvent;
            await backend.StartAsync(new AgentStartOptions
            {
                WorkingDirectory = cwd,
                Model = _model,
                ResumeSessionId = resume,
                ExtraArguments = _full ? [] : ["--strict-mcp-config", "--disable-slash-commands", "--tools", "Bash,Read,Write,Edit,Glob,Grep"],
            });
            Console.WriteLine($"[start] pid={backend.Process?.ProcessId} args: {string.Join(' ', backend.Process?.Arguments ?? [])}");
            return session;
        }

        public async Task<TurnResult?> TurnAsync(string prompt)
        {
            _text.Clear();
            _turn = new TaskCompletionSource<TurnResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.WriteLine($"[prompt] {prompt}");
            await Backend.SendPromptAsync(prompt);
            var finished = await Task.WhenAny(_turn.Task, Task.Delay(TimeSpan.FromSeconds(120)));
            if (finished != _turn.Task)
            {
                Console.WriteLine("[timeout] no result within 120 s");
                return null;
            }

            return await _turn.Task;
        }

        public async ValueTask DisposeAsync()
        {
            await Backend.DisposeAsync();
            if (_transcript is not null)
            {
                await _transcript.DisposeAsync();
            }
        }

        private void OnRaw(string line, bool outgoing)
        {
            if (_transcript is null)
            {
                return;
            }

            lock (_transcript)
            {
                // Our own stdin lines are kept too, wrapped so fixtures stay pure stdout.
                _transcript.WriteLine(outgoing ? "{\"nomad_stdin\":" + line + "}" : line);
                _transcript.Flush();
            }
        }

        private void OnEvent(AgentEvent e)
        {
            switch (e)
            {
                case SessionStartedEvent s:
                    Console.WriteLine($"[init] session={s.SessionId} model={s.Model} mode={s.PermissionMode} tools={s.Tools.Count} cli={s.AgentVersion}");
                    break;
                case AccountInfoEvent a:
                    Console.WriteLine($"[account] subscription={a.SubscriptionType} models={a.Models.Count}");
                    break;
                case TextDeltaEvent t:
                    _text.Append(t.Text);
                    Console.Write(t.Text);
                    OnText?.Invoke(_text.ToString());
                    break;
                case ThinkingDeltaEvent:
                    break;
                case BlockStartedEvent { Kind: AgentBlockKind.ToolUse } b:
                    Console.WriteLine($"\n[tool start] {b.ToolName} {b.ToolUseId}");
                    break;
                case BlockCompletedEvent { Kind: AgentBlockKind.Text } c:
                    Console.WriteLine($"\n[text block done] {c.Text?.Length} chars");
                    break;
                case BlockCompletedEvent { Kind: AgentBlockKind.Thinking } c:
                    Console.WriteLine($"[thinking block] {c.Text?.Length} chars");
                    break;
                case ToolCallEvent call:
                    Console.WriteLine($"[tool call] {call.Name} {call.Input}");
                    break;
                case ToolResultEvent r:
                    var text = r.Text.ReplaceLineEndings(" ");
                    Console.WriteLine($"[tool result] {(r.IsError ? "ERROR" : "ok")} {(text.Length > 120 ? text[..120] + "…" : text)}");
                    break;
                case PermissionRequestedEvent p:
                    PermissionCount++;
                    Console.WriteLine($"[permission] {p.Request.ToolName} {p.Request.Input} suggestions={p.Request.Suggestions}");
                    var decision = OnPermission?.Invoke(p.Request) ?? new PermissionDecision(PermissionChoice.Deny, "No handler in probe.");
                    _ = Backend.RespondAsync(p.Request, decision);
                    break;
                case PermissionCancelledEvent pc:
                    Console.WriteLine($"[permission cancelled] {pc.RequestId}");
                    break;
                case PermissionModeChangedEvent m:
                    Console.WriteLine($"[mode] {m.Mode}");
                    break;
                case ModelChangedEvent m:
                    Console.WriteLine($"[model] {m.Model}");
                    break;
                case TurnCompletedEvent done:
                    var r2 = done.Result;
                    Console.WriteLine($"\n[result] subtype={r2.Subtype} error={r2.IsError} interrupted={r2.Interrupted} {r2.DurationMs} ms turns={r2.NumTurns} tokens={r2.TotalTokens} (in {r2.InputTokens}, out {r2.OutputTokens}, cache r {r2.CacheReadTokens} w {r2.CacheCreationTokens}) {(r2.ErrorMessage is null ? "" : "msg=" + r2.ErrorMessage)}");
                    _turn.TrySetResult(r2);
                    break;
                case AgentErrorEvent err:
                    Console.WriteLine($"[error] {err.Message}");
                    break;
                case ProcessExitedEvent x:
                    Console.WriteLine($"[exit] code={x.ExitCode} expected={x.Expected}");
                    _turn.TrySetResult(null);
                    break;
            }
        }
    }
}
