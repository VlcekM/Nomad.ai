using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nomad.Core.Agents;

/// <summary>
/// How agents that take only command-line MCP servers (Antigravity, Codex) reach the chat tools and <see cref="AgentTools"/>. Nomad
/// listens on a named pipe; the agent starts <c>Nomad.ai.exe --chat-bridge &lt;pipe&gt; &lt;chat key&gt;</c>, which relays its stdin and
/// stdout to that pipe. The first line on the pipe is the chat key, so every call is answered for the chat that made it. The bridge
/// process only relays: every call runs in the Nomad window's own process, on the user's desktop, even when the agent started the
/// bridge on a hidden one.
/// </summary>
public static class ChatBridge
{
    /// <summary>
    /// The command-line switch that starts a copy of Nomad as a bridge process.
    /// </summary>
    public const string Argument = "--chat-bridge";

    #region Server (the running Nomad)

    /// <summary>
    /// The pipe this Nomad listens on; null until <see cref="Listen"/>.
    /// </summary>
    public static string? PipeName { get; private set; }

    /// <summary>
    /// Starts listening (once) for bridge processes, until the app exits.
    /// </summary>
    public static void Listen()
    {
        if (PipeName is not null) return;

        PipeName = $"nomad-chats-{Environment.ProcessId}";
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// The MCP server entry an ACP <c>session/new</c> takes, for the chat with <paramref name="chatKey"/>; null when not listening.
    /// </summary>
    public static JsonObject? AcpServer(string chatKey)
    {
        if (PipeName is not { } pipe || Environment.ProcessPath is not { } exe) return null;

        return new JsonObject
        {
            ["name"] = ServerName,
            ["command"] = exe,
            ["args"] = new JsonArray(Argument, pipe, chatKey),
            ["env"] = new JsonArray(),
        };
    }

    /// <summary>
    /// The server name both agents see the tools under (Antigravity calls them <c>mcp_nomad_chats_…</c>, Codex
    /// <c>mcp__nomad_chats__…</c>).
    /// </summary>
    public const string ServerName = "nomad_chats";

    /// <summary>
    /// The thread config a Codex <c>thread/start</c>, <c>thread/resume</c> or <c>thread/fork</c> takes to add the bridge as an MCP
    /// server for the chat with <paramref name="chatKey"/> (one app server runs every Codex chat, so each thread names its own key);
    /// null when not listening. Its tool time limit leaves room for the longest command <c>run_on_desktop</c> allows.
    /// </summary>
    public static JsonObject? CodexConfig(string chatKey)
    {
        if (PipeName is not { } pipe || Environment.ProcessPath is not { } exe) return null;

        return new JsonObject
        {
            ["mcp_servers." + ServerName] = new JsonObject
            {
                ["command"] = exe,
                ["args"] = new JsonArray(Argument, pipe, chatKey),
                ["tool_timeout_sec"] = (int)DesktopCommand.MaxTimeout.TotalSeconds + 60,
            },
        };
    }

    /// <summary>
    /// Accepts bridge processes one after another and serves each on its own task.
    /// </summary>
    private static async Task AcceptLoopAsync()
    {
        while (true)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName!, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A client that hung up mid-connect, or a pipe that could not be made: try again shortly, rather than stop
                // serving every chat for the rest of the session.
                pipe?.Dispose();
                AgentToolLog.Write($"The chat bridge's pipe failed ({ex.GetType().Name}: {ex.Message}); listening again.");
                await Task.Delay(1000).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    /// <summary>
    /// One bridge process: its chat key, then one JSON-RPC message per line each way.
    /// </summary>
    internal static async Task ServeAsync(Stream pipe)
    {
        await using var _ = pipe;
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        // Calls are answered as they finish, each on its own, so a long one (a command on the desktop) holds up no other.
        var writing = new SemaphoreSlim(1, 1);
        var calls = new List<Task>();
        // Cancelled when the bridge process goes away; each call's own token is also cancelled by MCP's notifications/cancelled.
        using var connection = new CancellationTokenSource();
        var pending = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        try
        {
            var key = await reader.ReadLineAsync().ConfigureAwait(false);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                calls.RemoveAll(c => c.IsCompleted);
                if (Parse(line) is not { } message) continue;

                if (CancelledRequest(message) is { } cancelled)
                {
                    try
                    {
                        if (pending.TryGetValue(cancelled, out var call)) await call.CancelAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // It finished just now.
                    }

                    continue;
                }

                var id = message.TryGetProperty("id", out var raw) ? raw.GetRawText() : null;
                var own = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                if (id is not null) pending[id] = own;
                calls.Add(Task.Run(async () =>
                {
                    try
                    {
                        await AnswerLineAsync(message, key, writer, writing, own.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (id is not null) pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(id, own));
                        own.Dispose();
                    }
                }));
            }
        }
        catch (IOException)
        {
            // The bridge process went away with its agent.
        }

        // Nobody is left to answer: stop what is still running (a command on the desktop) rather than let it run on.
        await connection.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(calls).ConfigureAwait(false);
    }

    /// <summary>
    /// The id (as raw JSON) of the request a <c>notifications/cancelled</c> message cancels; null for any other message.
    /// </summary>
    private static string? CancelledRequest(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && NomadSessionServer.Str(message, "method") == "notifications/cancelled" &&
        message.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty("requestId", out var id)
            ? id.GetRawText()
            : null;

    /// <summary>
    /// One line from a bridge process as JSON; null for a blank or unparsable line.
    /// </summary>
    private static JsonElement? Parse(string line)
    {
        if (line.Length == 0) return null;

        try
        {
            return JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Answers one message and writes the response, one response at a time; nothing for a call cancelled meanwhile, as MCP has it.
    /// </summary>
    private static async Task AnswerLineAsync(JsonElement message, string? key, StreamWriter writer, SemaphoreSlim writing,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await ChatTools.HandleAsync(message, key, cancellationToken).ConfigureAwait(false) is not { } response) return;
            if (cancellationToken.IsCancellationRequested) return;

            await writing.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
            }
            finally
            {
                writing.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The bridge process went away, or the agent cancelled the call, before the answer.
        }
    }

    #endregion

    #region Relay (the bridge process)

    /// <summary>
    /// The bridge process: relays stdin to the pipe and the pipe to stdout until either side closes. Returns the exit code.
    /// </summary>
    public static int Relay(string pipeName, string chatKey)
    {
        try
        {
            using var pipe = Connect(pipeName, chatKey);
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            var up = Task.Run(() => stdin.CopyToAsync(pipe));
            var down = Task.Run(() => CopyFlushingAsync(pipe, stdout));
            Task.WaitAny(up, down);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    /// <summary>
    /// Connects to Nomad's pipe and sends the chat key as its first line.
    /// </summary>
    private static NamedPipeClientStream Connect(string pipeName, string chatKey)
    {
        // CurrentUserOnly: the chat key and the agent's calls go only to a pipe the same user owns, never to another that took the name.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(5000);
            pipe.Write(Encoding.UTF8.GetBytes(chatKey + "\n"));
            pipe.Flush();
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies <paramref name="from"/> to <paramref name="to"/>, flushing after every read so each response reaches the agent at once.
    /// </summary>
    private static async Task CopyFlushingAsync(Stream from, Stream to)
    {
        var buffer = new byte[16384];
        int read;
        while ((read = await from.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            await to.FlushAsync().ConfigureAwait(false);
        }
    }

    #endregion
}
