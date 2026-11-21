using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nomad.Core.Agents;

/// <summary>
/// How agents that take only command-line MCP servers (Antigravity) reach the chat tools. Nomad listens on a named pipe; the agent
/// starts <c>Nomad.ai.exe --chat-bridge &lt;pipe&gt; &lt;chat key&gt;</c>, which relays its stdin and stdout to that pipe. The first line
/// on the pipe is the chat key, so every call is answered for the chat that made it.
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
            ["name"] = "nomad_chats",
            ["command"] = exe,
            ["args"] = new JsonArray(Argument, pipe, chatKey),
            ["env"] = new JsonArray(),
        };
    }

    /// <summary>
    /// Accepts bridge processes one after another and serves each on its own task.
    /// </summary>
    private static async Task AcceptLoopAsync()
    {
        while (true)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(PipeName!, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    /// <summary>
    /// One bridge process: its chat key, then one JSON-RPC message per line each way.
    /// </summary>
    private static async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        try
        {
            var key = await reader.ReadLineAsync().ConfigureAwait(false);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (await AnswerAsync(line, key).ConfigureAwait(false) is { } response)
                    await writer.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The bridge process went away with its agent.
        }
    }

    /// <summary>
    /// The response to one line from a bridge process; null for a blank or unparsable line, or a notification.
    /// </summary>
    private static async Task<JsonObject?> AnswerAsync(string line, string? key)
    {
        if (line.Length == 0) return null;

        JsonElement message;
        try
        {
            message = JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }

        return await ChatTools.HandleAsync(message, key).ConfigureAwait(false);
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
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
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
