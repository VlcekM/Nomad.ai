using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Process;

namespace Nomad.Core.Lsp;

/// <summary>An error answer from a language server.</summary>
public sealed class LspException(string message, long? code = null) : Exception(message)
{
    public long? Code { get; } = code;
}

/// <summary>
/// A language server process spoken to over stdio with the Language Server Protocol's framing: JSON-RPC messages, each
/// after a <c>Content-Length</c> header. Calls wait for their answer (and are cancelled on the server too when their
/// token is); the server's own requests go to <see cref="RequestHandler"/> (answered null when it has nothing to say),
/// its notifications to <see cref="Notification"/>.
/// </summary>
public sealed class LspConnection : IAsyncDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly Stream _input;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Queue<string> _stderrTail = new();
    private long _nextId;

    private LspConnection(System.Diagnostics.Process process)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
    }

    /// <summary>Every message in and out (for a log): its text, and whether it was sent.</summary>
    public Action<string, bool>? RawMessage { get; set; }

    /// <summary>The server's requests: method and params to the result (null for none). Runs on the reading thread.</summary>
    public Func<string, JsonElement, JsonNode?>? RequestHandler { get; set; }

    /// <summary>The server's notifications, on the reading thread.</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>The process ended (or its output closed).</summary>
    public event Action? Exited;

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

    /// <summary>The last lines it wrote to stderr, for an error message.</summary>
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

    /// <summary>Starts a server (killed with this app, as its other child processes are).</summary>
    public static LspConnection Start(string exe, IEnumerable<string> args, string workingDirectory, IDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }

        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} did not start.");
        }

        ChildProcessJob.TryAssign(process);
        var connection = new LspConnection(process);
        _ = Task.Run(connection.ReadAsync);
        _ = Task.Run(connection.ReadStderrAsync);
        return connection;
    }

    /// <summary>Calls a method and waits for its result; throws <see cref="LspException"/> on an error answer.</summary>
    public async Task<JsonElement> CallAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            await WriteAsync(message, CancellationToken.None).ConfigureAwait(false);
            return await reply.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Asked for no longer: the server may stop working on it.
            _ = NotifyAsync("$/cancelRequest", new JsonObject { ["id"] = id });
            throw;
        }
        catch (TimeoutException)
        {
            _ = NotifyAsync("$/cancelRequest", new JsonObject { ["id"] = id });
            throw new TimeoutException($"The language server did not answer {method}.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return WriteAsync(message, CancellationToken.None);
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var text = message.ToJsonString();
        var body = Encoding.UTF8.GetBytes(text);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                throw new InvalidOperationException("The language server is not running.");
            }

            RawMessage?.Invoke(text, true);
            await _input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _input.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("The language server is not running.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadAsync()
    {
        var output = _process.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024];
        var data = new MemoryStream();
        try
        {
            while (true)
            {
                // The headers: lines up to an empty one; only the length matters.
                var length = -1;
                while (await ReadLineAsync(output) is { } line)
                {
                    if (line.Length == 0)
                    {
                        break;
                    }

                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line.AsSpan(15).Trim(), out var n))
                    {
                        length = n;
                    }
                }

                if (length < 0)
                {
                    break; // The stream ended.
                }

                data.SetLength(0);
                while (data.Length < length)
                {
                    var read = await output.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - data.Length))).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    data.Write(buffer, 0, read);
                }

                var text = Encoding.UTF8.GetString(data.GetBuffer(), 0, length);
                RawMessage?.Invoke(text, false);
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    Dispatch(doc.RootElement);
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or EndOfStreamException)
        {
        }

        var gone = new InvalidOperationException("The language server stopped" + (StderrTail is { Length: > 0 } tail ? ":\n" + tail : "."));
        foreach (var reply in _pending.Values)
        {
            reply.TrySetException(gone);
        }

        Exited?.Invoke();
    }

    // A header line (ASCII, ended by CRLF), or null at the stream's end.
    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one).ConfigureAwait(false) == 0)
            {
                return null;
            }

            if (one[0] == '\n')
            {
                return line.ToString().TrimEnd('\r');
            }

            line.Append((char)one[0]);
        }
    }

    private void Dispatch(JsonElement message)
    {
        var method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var parameters = message.TryGetProperty("params", out var p) ? p.Clone() : default;
        if (method is null)
        {
            if (hasId && id.ValueKind == JsonValueKind.Number && _pending.TryRemove(id.GetInt64(), out var reply))
            {
                if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    var text = error.TryGetProperty("message", out var em) ? em.GetString() : null;
                    var code = error.TryGetProperty("code", out var ec) && ec.TryGetInt64(out var c) ? c : (long?)null;
                    reply.TrySetException(new LspException(text ?? "The language server reported an error.", code));
                }
                else
                {
                    reply.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : default);
                }
            }

            return;
        }

        try
        {
            if (hasId)
            {
                JsonNode? result = null;
                try
                {
                    result = RequestHandler?.Invoke(method, parameters);
                }
                catch (Exception)
                {
                    // Answered null all the same: the server must not wait for it.
                }

                _ = WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result }, CancellationToken.None);
                return;
            }

            Notification?.Invoke(method, parameters);
        }
        catch (Exception)
        {
            // A faulty handler must not stop the pump.
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

    /// <summary>Asks the server to shut down and exit, and kills it if it hasn't after a moment.</summary>
    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
        {
            try
            {
                await CallAsync("shutdown", null, timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                await NotifyAsync("exit", null).ConfigureAwait(false);
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }
        }

        _process.Dispose();
    }
}
