using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nomad.Core.Process;

namespace Nomad.Core.Codex;

/// <summary>What a Codex thread's backend hears from the server: the thread's notifications and requests, in order.</summary>
public interface ICodexThreadListener
{
    void OnNotification(string method, JsonElement parameters);

    /// <summary>A request the server waits on (an approval, a question): answer it with <see cref="CodexServer.RespondAsync"/>.</summary>
    void OnRequest(JsonElement id, string method, JsonElement parameters);

    void OnServerExited();
}

/// <summary>A JSON-RPC error the server answered a call with.</summary>
public sealed class CodexException(string message, long? code = null) : Exception(message)
{
    public long? Code { get; } = code;
}

/// <summary>
/// The one <c>codex app-server</c> this app runs, shared by every Codex session and by the session list: JSON-RPC over
/// its stdin and stdout, one message per line. Notifications and requests that name a thread go to that thread's
/// listener, on the reading thread and in order. Started on first use; ends with the app.
/// </summary>
public sealed partial class CodexServer : IAsyncDisposable
{
    private static readonly SemaphoreSlim StartLock = new(1, 1);
    private static CodexServer? _shared;

    private readonly System.Diagnostics.Process _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, ICodexThreadListener> _threads = new(StringComparer.Ordinal);
    private readonly Queue<string> _stderrTail = new();
    private long _nextId;

    private CodexServer(System.Diagnostics.Process process)
    {
        _process = process;
    }

    /// <summary>The configured path or command name (Settings); "codex" when empty.</summary>
    public static string? CliPath { get; set; }

    /// <summary>Every line in and out (<c>--dev-log</c>): the line, and whether it was sent.</summary>
    public static Action<string, bool>? RawLine { get; set; }

    /// <summary>Whether Codex looks installed (its executable resolves); cheap, no process started.</summary>
    public static bool IsInstalled => ResolveExecutable(CliPath) is not null;

    /// <summary>Codex has been used on this machine (it has stored sessions): only then is its server started to list them.</summary>
    public static bool HasData => Directory.Exists(Path.Combine(CodexHome, "sessions"));

    /// <summary>Codex's home folder (<c>CODEX_HOME</c>, else <c>~/.codex</c>).</summary>
    public static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>The model context windows the server reported with token usage, by model id.</summary>
    public static ConcurrentDictionary<string, long> ContextWindows { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Codex version the server reported, e.g. "0.163.0".</summary>
    public string? Version { get; private set; }

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Every notification, on the reading thread (rate limits, account changes: those name no thread).</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>The running server, started if needed. Throws when Codex is not installed or does not come up.</summary>
    public static async Task<CodexServer> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_shared is { IsRunning: true } running)
        {
            return running;
        }

        await StartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shared is { IsRunning: true } again)
            {
                return again;
            }

            _shared?.Kill();
            _shared = await StartAsync(cancellationToken).ConfigureAwait(false);
            return _shared;
        }
        finally
        {
            StartLock.Release();
        }
    }

    /// <summary>The server if one runs now, without starting it.</summary>
    public static CodexServer? Current => _shared is { IsRunning: true } s ? s : null;

    /// <summary>Kills the shared server (app shutdown).</summary>
    public static void KillShared() => _shared?.Kill();

    /// <summary>Sends this thread's notifications and requests to <paramref name="listener"/> from now on.</summary>
    public void Attach(string threadId, ICodexThreadListener listener) => _threads[threadId] = listener;

    public void Detach(string threadId, ICodexThreadListener listener) =>
        ((ICollection<KeyValuePair<string, ICodexThreadListener>>)_threads).Remove(new(threadId, listener));

    /// <summary>Calls a method and waits for its result. Throws <see cref="CodexException"/> on an error answer.</summary>
    public async Task<JsonElement> CallAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            var message = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            await WriteAsync(message, cancellationToken).ConfigureAwait(false);
            return await reply.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Codex did not answer {method}.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Answers a request of the server's.</summary>
    public Task RespondAsync(JsonElement id, JsonNode result, CancellationToken cancellationToken = default) =>
        WriteAsync(new JsonObject { ["id"] = CodexJson.ToNode(id), ["result"] = result }, cancellationToken);

    /// <summary>Turns a request of the server's down with an error.</summary>
    public Task RespondErrorAsync(JsonElement id, string message, CancellationToken cancellationToken = default) =>
        WriteAsync(new JsonObject { ["id"] = CodexJson.ToNode(id), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = message } }, cancellationToken);

    /// <summary>The last lines Codex wrote to stderr, for an error message.</summary>
    public string StderrTail
    {
        get
        {
            lock (_stderrTail)
            {
                return string.Join("\n", _stderrTail);
            }
        }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var line = message.ToJsonString();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                throw new InvalidOperationException("Codex is not running.");
            }

            RawLine?.Invoke(line, true);
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Codex is not running.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<CodexServer> StartAsync(CancellationToken cancellationToken)
    {
        var exe = ResolveExecutable(CliPath)
            ?? throw new FileNotFoundException($"Could not find Codex ('{(string.IsNullOrWhiteSpace(CliPath) ? "codex" : CliPath)}'). Install it (npm i -g @openai/codex), or set its path in Settings.");

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (CliLocator.IsBatchFile(exe))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = $"/d /s /c \"\"{exe}\" app-server\"";
        }
        else
        {
            psi.FileName = exe;
            psi.ArgumentList.Add("app-server");
        }

        // No colour codes in its log lines (they end up in error messages).
        psi.Environment["NO_COLOR"] = "1";

        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Codex did not start.");
        }

        ChildProcessJob.TryAssign(process);
        var server = new CodexServer(process);
        _ = Task.Run(server.ReadStdoutAsync);
        _ = Task.Run(server.ReadStderrAsync);

        try
        {
            var init = await server.CallAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "nomad", ["title"] = "Nomad", ["version"] = typeof(CodexServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" },
                ["capabilities"] = new JsonObject
                {
                    ["experimentalApi"] = true,
                    ["requestAttestation"] = false,
                    ["optOutNotificationMethods"] = new JsonArray(
                        "turn/moderationMetadata", "mcpServer/startupStatus/updated", "rawResponseItem/completed", "rawResponse/completed",
                        "item/commandExecution/outputDelta", "item/fileChange/outputDelta", "thread/readState/changed"),
                },
            }, cancellationToken, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            server.Version = VersionIn().Match(CodexJson.Str(init, "userAgent") ?? "") is { Success: true } m ? m.Groups[1].Value : null;
            await server.WriteAsync(new JsonObject { ["method"] = "initialized" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var tail = server.StderrTail;
            server.Kill();
            throw new InvalidOperationException("Codex did not start" + (tail.Length > 0 ? ":\n" + tail : "."), ex);
        }

        return server;
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                RawLine?.Invoke(line, false);
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    Dispatch(doc.RootElement);
                }
                catch (JsonException)
                {
                    // Not a message (a stray log line): skip it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        var gone = new InvalidOperationException("Codex stopped" + (StderrTail is { Length: > 0 } t ? ":\n" + t : "."));
        foreach (var reply in _pending.Values)
        {
            reply.TrySetException(gone);
        }

        foreach (var listener in _threads.Values.Distinct())
        {
            try
            {
                listener.OnServerExited();
            }
            catch (Exception)
            {
                // A faulty listener must not stop the others hearing it.
            }
        }
    }

    private void Dispatch(JsonElement message)
    {
        var method = CodexJson.Str(message, "method");
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var parameters = CodexJson.Keep(CodexJson.Any(message, "params"));

        if (method is null)
        {
            // An answer to one of our calls.
            if (hasId && id.ValueKind == JsonValueKind.Number && _pending.TryRemove(id.GetInt64(), out var reply))
            {
                if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    reply.TrySetException(new CodexException(CodexJson.Str(error, "message") ?? "Codex reported an error.", CodexJson.Long(error, "code")));
                }
                else
                {
                    reply.TrySetResult(CodexJson.Keep(CodexJson.Any(message, "result")));
                }
            }

            return;
        }

        var listener = CodexJson.Str(parameters, "threadId") is { } threadId && _threads.TryGetValue(threadId, out var l) ? l : null;
        try
        {
            if (hasId)
            {
                var requestId = id.Clone();
                if (listener is not null)
                {
                    listener.OnRequest(requestId, method, parameters);
                }
                else
                {
                    _ = RespondErrorAsync(requestId, $"Nomad does not handle {method} here.");
                }

                return;
            }

            listener?.OnNotification(method, parameters);
            Notification?.Invoke(method, parameters);
        }
        catch (Exception)
        {
            // A faulty listener must not stop the pump.
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > 20)
                    {
                        _stderrTail.Dequeue();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// The Codex executable for a configured path or command name, or null: on PATH, else where its installer puts
    /// it. An npm shim (<c>codex.cmd</c>) is followed to the native binary it starts, so no node sits in between.
    /// </summary>
    public static string? ResolveExecutable(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "codex" : configured;
        var exe = CliLocator.Resolve(name);
        if (exe is null && string.IsNullOrWhiteSpace(configured) && OperatingSystem.IsWindows())
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe");
            exe = File.Exists(installed) ? installed : null;
        }

        if (exe is not null && CliLocator.IsBatchFile(exe))
        {
            var vendor = Path.Combine(Path.GetDirectoryName(exe)!, "node_modules", "@openai", "codex", "vendor");
            if (Directory.Exists(vendor))
            {
                var binary = Directory.EnumerateFiles(vendor, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (binary is not null)
                {
                    return binary;
                }
            }
        }

        return exe;
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        Kill();
        _process.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex(@"/(\d+\.\d+\.\d+)")]
    private static partial Regex VersionIn();
}
