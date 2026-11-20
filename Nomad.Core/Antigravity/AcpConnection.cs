using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Codex;
using AgentProcess = System.Diagnostics.Process;

namespace Nomad.Core.Antigravity;

/// <summary>
/// Exchanges newline-delimited JSON-RPC messages with an ACP agent.
/// </summary>
internal sealed class AcpConnection : IAsyncDisposable
{
    /// <summary>
    /// The agent subprocess and its redirected streams.
    /// </summary>
    private readonly AgentProcess _process;

    /// <summary>
    /// Requests waiting for a response from the agent.
    /// </summary>
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    /// <summary>
    /// Keeps complete messages together on stdin.
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1);

    /// <summary>
    /// The stdout pump, drained on shutdown.
    /// </summary>
    private readonly Task _reader;

    /// <summary>
    /// The stderr pump, drained on shutdown.
    /// </summary>
    private readonly Task _stderr;

    /// <summary>
    /// Monotonic request identifier.
    /// </summary>
    private int _nextId;

    /// <summary>
    /// Set when the client initiates shutdown.
    /// </summary>
    private int _stopping;

    /// <summary>
    /// Starts the agent with UTF-8 stdio in the workspace.
    /// </summary>
    public AcpConnection(string executable, string cwd, Func<JsonElement, Task> receive, Action<int, bool> exited,
        IReadOnlyList<string>? additionalDirectories = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("acp");
        foreach (var directory in additionalDirectories ?? [])
        {
            start.ArgumentList.Add("--add-dir");
            start.ArgumentList.Add(directory);
        }

        _process = AgentProcess.Start(start) ?? throw new InvalidOperationException("Could not start Antigravity CLI.");
        _stderr = _process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        _reader = Task.Run(() => ReadAsync(receive, exited));
    }

    /// <summary>
    /// Whether the agent process is accepting requests.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _stopping) == 0 && !_process.HasExited;

    /// <summary>
    /// Sends a request and awaits its response.
    /// </summary>
    public async Task<JsonElement> CallAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;

        try
        {
            await WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken)
                .ConfigureAwait(false);
            return await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Sends a notification without waiting for a response.
    /// </summary>
    public Task NotifyAsync(string method, JsonObject parameters, CancellationToken cancellationToken) =>
        WriteAsync(new JsonObject { ["method"] = method, ["params"] = parameters }, cancellationToken);

    /// <summary>
    /// Answers a request from the agent.
    /// </summary>
    public Task ReplyAsync(JsonElement id, JsonObject result, CancellationToken cancellationToken = default) =>
        WriteAsync(new JsonObject { ["id"] = CodexJson.ToNode(id), ["result"] = result }, cancellationToken);

    /// <summary>
    /// Declines an unsupported client method.
    /// </summary>
    public Task UnsupportedAsync(JsonElement id) => WriteAsync(new JsonObject
    {
        ["id"] = CodexJson.ToNode(id),
        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not supported by Nomad." },
    }, CancellationToken.None);

    /// <summary>
    /// Closes stdin, then kills the process tree if the agent stays open.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _process.StandardInput.Close();
        }
        finally
        {
            _writeGate.Release();
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }

        await Task.WhenAll(_reader, _stderr).ConfigureAwait(false);
        _process.Dispose();
    }

    /// <summary>
    /// Serializes a complete message and flushes it to the agent.
    /// </summary>
    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        message["jsonrpc"] = "2.0";
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning) throw new InvalidOperationException("Antigravity CLI has stopped.");

            await _process.StandardInput.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Dispatches responses, notifications and server requests in wire order.
    /// </summary>
    private async Task ReadAsync(Func<JsonElement, Task> receive, Action<int, bool> exited)
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                if (CodexJson.Str(root, "method") is not null)
                {
                    await receive(root).ConfigureAwait(false);
                }
                else
                {
                    CompleteRequest(root);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            foreach (var reply in _pending.Values) reply.TrySetException(ex);
        }
        finally
        {
            foreach (var reply in _pending.Values)
                reply.TrySetException(new InvalidOperationException("Antigravity CLI closed its protocol stream."));

            exited(_process.HasExited ? _process.ExitCode : 1, Volatile.Read(ref _stopping) != 0);
        }
    }

    /// <summary>
    /// Resolves a request with the returned value or error.
    /// </summary>
    private void CompleteRequest(JsonElement message)
    {
        if (CodexJson.Long(message, "id") is not { } id || !_pending.TryGetValue((int)id, out var reply)) return;

        var error = CodexJson.Obj(message, "error");
        if (error.ValueKind != JsonValueKind.Undefined)
        {
            reply.TrySetException(new InvalidOperationException(CodexJson.Str(error, "message") ?? "Antigravity request failed."));
        }
        else
        {
            reply.TrySetResult(CodexJson.Keep(CodexJson.Any(message, "result")));
        }
    }
}
