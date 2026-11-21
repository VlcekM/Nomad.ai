using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Nomad.Core.Agents;

/// <summary>
/// How agents that take only command-line MCP servers (Antigravity) reach the chat tools. Nomad listens on a named pipe; the agent
/// starts <c>Nomad.ai.exe --chat-bridge &lt;pipe&gt; &lt;chat key&gt;</c>, which relays its stdin and stdout to that pipe. The first line
/// on the pipe is the chat key, so every call is answered for the chat that made it.
/// </summary>
public static class ChatBridge
{
    public const string Argument = "--chat-bridge";

    /// <summary>The pipe this Nomad listens on; null until <see cref="Listen"/>.</summary>
    public static string? PipeName { get; private set; }

    /// <summary>The MCP server entry an ACP <c>session/new</c> takes, for the chat with <paramref name="chatKey"/>; null when not listening.</summary>
    public static System.Text.Json.Nodes.JsonObject? AcpServer(string chatKey) =>
        PipeName is { } pipe && Environment.ProcessPath is { } exe
            ? new()
            {
                ["name"] = "nomad_chats",
                ["command"] = exe,
                ["args"] = new System.Text.Json.Nodes.JsonArray(Argument, pipe, chatKey),
                ["env"] = new System.Text.Json.Nodes.JsonArray(),
            }
            : null;

    /// <summary>Starts listening (once) for bridge processes, until the app exits.</summary>
    public static void Listen()
    {
        if (PipeName is not null)
        {
            return;
        }

        PipeName = $"nomad-chats-{Environment.ProcessId}";
        _ = Task.Run(AcceptLoopAsync);
    }

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

    /// <summary>One bridge process: its chat key, then one JSON-RPC message per line each way.</summary>
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
                if (line.Length == 0)
                {
                    continue;
                }

                JsonElement message;
                try
                {
                    message = JsonDocument.Parse(line).RootElement;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (await ChatTools.HandleAsync(message, key).ConfigureAwait(false) is { } response)
                {
                    await writer.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
                }
            }
        }
        catch (IOException)
        {
            // The bridge process went away with its agent.
        }
    }

    /// <summary>
    /// The bridge process: relays stdin to the pipe and the pipe to stdout until either side closes. Returns the exit code.
    /// </summary>
    public static int Relay(string pipeName, string chatKey)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5000);
            var key = Encoding.UTF8.GetBytes(chatKey + "\n");
            pipe.Write(key);
            pipe.Flush();

            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            var up = Task.Run(async () =>
            {
                await stdin.CopyToAsync(pipe).ConfigureAwait(false);
            });
            var down = Task.Run(async () =>
            {
                var buffer = new byte[16384];
                int read;
                while ((read = await pipe.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await stdout.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    await stdout.FlushAsync().ConfigureAwait(false);
                }
            });
            Task.WaitAny(up, down);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return 1;
        }
    }
}
