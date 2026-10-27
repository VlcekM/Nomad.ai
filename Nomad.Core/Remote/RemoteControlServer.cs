using System.Text.Json;
using Nomad.Core.Process;

namespace Nomad.Core.Remote;

public enum RemoteControlServerState
{
    Stopped,
    Starting,
    Running,

    /// <summary>The CLI wants a one-time answer first (Remote Control consent, or trusting the folder).</summary>
    NeedsSetup,
    Failed,
}

/// <summary>
/// A <c>claude remote-control</c> server for one folder: it registers the folder with claude.ai, so
/// the Claude mobile app and claude.ai/code can start sessions in it. Each session it starts is a
/// child process (<c>claude --print --sdk-url … --session-id …</c>) that talks to Anthropic directly;
/// Nomad finds them with <see cref="RemoteSessionScanner"/> and shows their transcripts.
/// </summary>
public sealed class RemoteControlServer : IAsyncDisposable
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(20);

    private readonly string _cliPath;
    private readonly string? _permissionMode;
    private ClaudeCliProcess? _process;
    private CancellationTokenSource? _restart;
    private bool _stopping;

    public RemoteControlServer(string folder, string cliPath, string? permissionMode)
    {
        Folder = folder;
        _cliPath = cliPath;
        _permissionMode = permissionMode;
    }

    public string Folder { get; }

    public RemoteControlServerState State { get; private set; }

    /// <summary>The server's PID while it runs (the parent of the sessions it starts).</summary>
    public int? ProcessId => _process is { IsRunning: true } p ? p.ProcessId : null;

    /// <summary>Why the server is not running (setup needed, last error), for the settings page.</summary>
    public string? Message { get; private set; }

    /// <summary>Raised on a background thread whenever <see cref="State"/> or <see cref="Message"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Starts the server unless the CLI first needs the user's one-time answer.</summary>
    public void Start()
    {
        _stopping = false;
        _restart?.Cancel();
        if (_process is { IsRunning: true })
        {
            return;
        }

        if (!Directory.Exists(Folder))
        {
            Set(RemoteControlServerState.Failed, "The folder does not exist.");
            return;
        }

        // Without a terminal the CLI cannot ask, so these answers have to be given in one first.
        if (!HasSeenRemoteDialog())
        {
            Set(RemoteControlServerState.NeedsSetup, "Remote Control has not been turned on in Claude Code yet.");
            return;
        }

        var args = new List<string> { "remote-control", "--spawn", "same-dir", "--no-create-session-in-dir" };
        if (!string.IsNullOrEmpty(_permissionMode) && _permissionMode != "default")
        {
            args.Add("--permission-mode");
            args.Add(_permissionMode);
        }

        var process = new ClaudeCliProcess(new ClaudeCliOptions { CliPath = _cliPath, WorkingDirectory = Folder }, args);
        process.StdoutLine += OnOutput;
        process.StderrLine += OnOutput;
        process.Exited += code => OnExited(process, code);
        Set(RemoteControlServerState.Starting, null);
        try
        {
            _process = process;
            process.Start();
        }
        catch (Exception ex)
        {
            _process = null;
            Set(RemoteControlServerState.Failed, ex.Message);
            return;
        }

        // The server prints nothing reliable once it is up; still alive after a few seconds is up.
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
        {
            if (ReferenceEquals(_process, process) && process.IsRunning && State == RemoteControlServerState.Starting)
            {
                Set(RemoteControlServerState.Running, null);
            }
        }, TaskScheduler.Default);
    }

    public async Task StopAsync()
    {
        _stopping = true;
        _restart?.Cancel();
        var process = _process;
        _process = null;
        if (process is not null)
        {
            // Ctrl+C lets it unregister the folder; killed, claude.ai keeps the folder "served" for
            // a while and the next server has to wait.
            if (process.TrySendCtrlC())
            {
                await Task.WhenAny(process.Completion, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
            }

            await process.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
        }

        Set(RemoteControlServerState.Stopped, null);
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void OnOutput(string line)
    {
        if (line.Contains("Ready", StringComparison.Ordinal) && State == RemoteControlServerState.Starting)
        {
            Set(RemoteControlServerState.Running, null);
        }
        else if (line.Contains("Workspace not trusted", StringComparison.OrdinalIgnoreCase))
        {
            Set(RemoteControlServerState.NeedsSetup, "Claude Code does not trust this folder yet.");
        }
        else if (line.Contains("already served", StringComparison.OrdinalIgnoreCase))
        {
            // Another `claude remote-control` (a terminal) has the folder; retried until it stops.
            Set(RemoteControlServerState.Failed, "A claude remote-control in a terminal already serves this folder.");
        }
        else if (line.Contains("Enable Remote Control?", StringComparison.OrdinalIgnoreCase))
        {
            Set(RemoteControlServerState.NeedsSetup, "Remote Control has not been turned on in Claude Code yet.");
            _process?.Kill();
        }
    }

    private void OnExited(ClaudeCliProcess process, int code)
    {
        if (!ReferenceEquals(_process, process))
        {
            return;
        }

        _process = null;
        if (_stopping || State == RemoteControlServerState.NeedsSetup)
        {
            return;
        }

        var tail = process.StderrTail.Trim();
        Set(RemoteControlServerState.Failed, tail.Length > 0 ? LastLine(tail) : $"Claude Code stopped (code {code}). Retrying…");

        // Network trouble and the like: try again a little later.
        var cts = new CancellationTokenSource();
        _restart = cts;
        _ = Task.Delay(RestartDelay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && !_stopping)
            {
                Start();
            }
        }, TaskScheduler.Default);
    }

    private void Set(RemoteControlServerState state, string? message)
    {
        if (State == state && Message == message)
        {
            return;
        }

        State = state;
        Message = message;
        Changed?.Invoke();
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? text;

    /// <summary>The CLI remembers the one-time Remote Control dialog in <c>~/.claude.json</c>.</summary>
    private static bool HasSeenRemoteDialog()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("remoteDialogSeen", out var seen) && seen.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unknown: try, and let the server's own output tell.
            return true;
        }
    }
}
