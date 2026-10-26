using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Klodi.Core.Process;

/// <summary>
/// One running <c>claude -p --input-format stream-json --output-format stream-json</c> process:
/// spawns it, pumps stdout/stderr line by line on background threads, serialises writes to stdin,
/// and tracks its PID so we only ever stop processes we started ourselves.
/// </summary>
public sealed class ClaudeCliProcess : IAsyncDisposable
{
    /// <summary>
    /// Variables a parent Claude Code session sets that would make the child think it is nested
    /// inside another session. Removed from the child's environment; everything else is inherited so
    /// the CLI's stored login keeps working.
    /// </summary>
    public static readonly IReadOnlyList<string> RemovedEnvironmentVariables = ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT"];

    private static readonly ConcurrentDictionary<int, ClaudeCliProcess> Owned = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Queue<string> _stderrTail = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private System.Diagnostics.Process? _process;
    private bool _inputClosed;

    public ClaudeCliProcess(ClaudeCliOptions options)
    {
        Options = options;
        Arguments = BuildArguments(options);
    }

    public ClaudeCliOptions Options { get; }

    /// <summary>The CLI arguments (without the executable).</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Full path of the executable actually started.</summary>
    public string? ExecutablePath { get; private set; }

    public int? ProcessId { get; private set; }

    public bool IsRunning => _process is not null && !_exited.Task.IsCompleted;

    /// <summary>Completes with the exit code once the process exited and stdout was drained.</summary>
    public Task<int> Completion => _exited.Task;

    /// <summary>Raised on a background thread for every stdout line.</summary>
    public event Action<string>? StdoutLine;

    /// <summary>Raised on a background thread for every stderr line.</summary>
    public event Action<string>? StderrLine;

    /// <summary>Raised once, after stdout reached EOF and the process exited.</summary>
    public event Action<int>? Exited;

    /// <summary>The last stderr lines, for error messages.</summary>
    public string StderrTail
    {
        get
        {
            lock (_stderrTail)
            {
                return string.Join('\n', _stderrTail);
            }
        }
    }

    /// <summary>PIDs of CLI processes started by this app that are still alive.</summary>
    public static IReadOnlyCollection<int> OwnedProcessIds => Owned.Keys.ToArray();

    public static List<string> BuildArguments(ClaudeCliOptions o)
    {
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
        };

        if (o.IncludePartialMessages)
        {
            args.Add("--include-partial-messages");
        }

        if (o.PermissionPromptOverStdio)
        {
            args.Add("--permission-prompt-tool");
            args.Add("stdio");
        }

        if (!string.IsNullOrWhiteSpace(o.Model))
        {
            args.Add("--model");
            args.Add(o.Model);
        }

        if (!string.IsNullOrWhiteSpace(o.PermissionMode))
        {
            args.Add("--permission-mode");
            args.Add(o.PermissionMode);
        }

        if (o.AllowBypassPermissions)
        {
            args.Add("--allow-dangerously-skip-permissions");
        }

        if (!string.IsNullOrWhiteSpace(o.Effort))
        {
            args.Add("--effort");
            args.Add(o.Effort);
        }

        if (!string.IsNullOrWhiteSpace(o.ThinkingDisplay))
        {
            args.Add("--thinking-display");
            args.Add(o.ThinkingDisplay);
        }

        if (!string.IsNullOrWhiteSpace(o.ResumeSessionId))
        {
            args.Add("--resume");
            args.Add(o.ResumeSessionId);
        }

        args.AddRange(o.ExtraArguments);
        return args;
    }

    /// <summary>Starts the process. Throws <see cref="FileNotFoundException"/> when the CLI is missing.</summary>
    public void Start()
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("The process was already started.");
        }

        var exe = CliLocator.Resolve(Options.CliPath)
            ?? throw new FileNotFoundException($"Could not find the Claude Code CLI ('{Options.CliPath}'). Install it and log in, or set its path in Settings.");
        ExecutablePath = exe;

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };

        if (CliLocator.IsBatchFile(exe))
        {
            // npm-style shims must run through cmd.exe; our arguments never contain cmd metacharacters
            // except inside quoted values, which we quote.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var sb = new StringBuilder("/d /s /c \"");
            sb.Append(QuoteForCmd(exe));
            foreach (var a in Arguments)
            {
                sb.Append(' ').Append(QuoteForCmd(a));
            }

            sb.Append('"');
            psi.Arguments = sb.ToString();
        }
        else
        {
            psi.FileName = exe;
            foreach (var a in Arguments)
            {
                psi.ArgumentList.Add(a);
            }
        }

        foreach (var name in RemovedEnvironmentVariables)
        {
            psi.Environment.Remove(name);
        }

        var process = new System.Diagnostics.Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("The Claude Code CLI did not start.");
        }

        _process = process;
        ProcessId = process.Id;
        Owned[process.Id] = this;
        ChildProcessJob.TryAssign(process);

        _ = Task.Run(() => PumpStderrAsync(process));
        _ = Task.Run(() => PumpStdoutAsync(process));
    }

    /// <summary>Writes one protocol line (a JSON object without trailing newline) to stdin.</summary>
    public async Task WriteLineAsync(string jsonLine, CancellationToken cancellationToken = default)
    {
        var process = _process ?? throw new InvalidOperationException("The process is not running.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_inputClosed || _exited.Task.IsCompleted)
            {
                throw new InvalidOperationException("The Claude Code process is no longer accepting input.");
            }

            var stdin = process.StandardInput;
            await stdin.WriteAsync(jsonLine.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("The Claude Code process closed its input.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Closes stdin; the CLI finishes the current turn and exits.</summary>
    public void CloseInput()
    {
        _writeLock.Wait();
        try
        {
            if (_inputClosed || _process is null)
            {
                return;
            }

            _inputClosed = true;
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Closes stdin and waits for a graceful exit; kills the process tree after the grace period.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        if (_process is null || _exited.Task.IsCompleted)
        {
            return;
        }

        CloseInput();
        var finished = await Task.WhenAny(_exited.Task, Task.Delay(grace)).ConfigureAwait(false);
        if (finished != _exited.Task)
        {
            Kill();
            await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
    }

    /// <summary>Kills this process and its children (never anything we did not start).</summary>
    public void Kill()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Kills every CLI process this app started that is still running (app shutdown).</summary>
    public static void KillAllOwned()
    {
        foreach (var p in Owned.Values)
        {
            p.Kill();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        _process?.Dispose();
    }

    private async Task PumpStdoutAsync(System.Diagnostics.Process process)
    {
        try
        {
            var reader = process.StandardOutput;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    StdoutLine?.Invoke(line);
                }
                catch (Exception ex)
                {
                    AddStderr($"[klodi] stdout handler failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        int exitCode;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        Owned.TryRemove(process.Id, out _);
        _inputClosed = true;
        _exited.TrySetResult(exitCode);
        Exited?.Invoke(exitCode);
    }

    private async Task PumpStderrAsync(System.Diagnostics.Process process)
    {
        try
        {
            var reader = process.StandardError;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                AddStderr(line);
                StderrLine?.Invoke(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private void AddStderr(string line)
    {
        lock (_stderrTail)
        {
            _stderrTail.Enqueue(line);
            while (_stderrTail.Count > 40)
            {
                _stderrTail.Dequeue();
            }
        }
    }

    private static string QuoteForCmd(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', '%', '!']) < 0)
        {
            return arg;
        }

        return "\"" + arg.Replace("\"", "\"\"") + "\"";
    }
}
